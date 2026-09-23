using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CacheTower.Providers.Memory;
using CacheTower.Serializers;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CacheTower.Tests.StaleWhileRevalidate;

/// <summary>
/// Regression material for the contract "CacheTower: stale-while-revalidate, single-flight refresh and origin receipts".
/// Every case enters through the public <see cref="ICacheStack"/> API using a fake clock and controllable cache layers;
/// no internal result object is faked. Fixed inputs cover concurrent reads, factory failure, the eviction/refresh race,
/// a corrupt backing layer, window endpoints and caller cancellation, with the default strategy kept fully compatible.
/// </summary>
[TestClass]
public class SwrContractTests
{
	private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(10);
	private static readonly TimeSpan SwrWindow = TimeSpan.FromSeconds(5);
	private const int ConcurrentReaders = 100;

	private static CacheSettings Settings => new(Ttl, TimeSpan.FromSeconds(2));
	private static CacheReadOptions ReadOptions => new(SwrWindow);

	private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
	{
		var deadline = DateTime.UtcNow + timeout;
		while (DateTime.UtcNow < deadline)
		{
			if (condition())
			{
				return true;
			}
			await Task.Delay(10);
		}
		return condition();
	}

	private static void AssertReceipt(CacheReadResult<int> result, int expectedValue, CacheValueOrigin expectedOrigin, int expectedLayer, string context)
	{
		Assert.AreEqual(expectedValue, result.Value, $"[{context}] value mismatch");
		Assert.AreEqual(expectedOrigin, result.Receipt.Origin, $"[{context}] origin mismatch - {result.Receipt}");
		Assert.AreEqual(expectedLayer, result.Receipt.LayerIndex, $"[{context}] layer index mismatch - {result.Receipt}");
	}

	[TestMethod]
	public async Task Swr_DefaultStrategyWithoutReadOptions_RemainsFullyCompatible()
	{
		var clock = FakeClock.Start();
		try
		{
			await using var stack = new CacheStack(null, new(new[] { new MemoryCacheLayer() }));

			//Fresh hit: factory never invoked
			var freshCalls = 0;
			await stack.SetAsync("fresh", 1, Ttl);
			var fresh = await stack.GetOrSetAsync("fresh", _ => { Interlocked.Increment(ref freshCalls); return Task.FromResult(99); }, Settings);
			Assert.AreEqual(1, fresh, "default strategy must return the existing fresh value");
			Assert.AreEqual(0, freshCalls, "default strategy must not invoke the factory on a fresh hit");

			//Expired hit: default strategy never serves expired data and refreshes in the foreground
			var expiredCalls = 0;
			await stack.SetAsync("expired", 7, Ttl);
			clock.Advance(Ttl + SwrWindow);
			var expired = await stack.GetOrSetAsync("expired", old =>
			{
				Interlocked.Increment(ref expiredCalls);
				Assert.AreEqual(7, old, "default foreground refresh must pass the previous value to the factory");
				return Task.FromResult(8);
			}, Settings);
			Assert.AreEqual(8, expired, "default strategy must refresh expired data even within what would be the SWR window");
			Assert.AreEqual(1, expiredCalls, "default strategy must invoke the factory once for expired data");
		}
		finally
		{
			clock.Reset();
		}
	}

	[TestMethod]
	public async Task Swr_NegativeWindow_IsRejectedWithExistingErrorLayering_AndRepeatable()
	{
		await using var stack = new CacheStack(null, new(new[] { new MemoryCacheLayer() }));

		Exception? first = null;
		Exception? second = null;
		try
		{
			_ = new CacheReadOptions(TimeSpan.FromSeconds(-1));
		}
		catch (Exception e)
		{
			first = e;
		}
		try
		{
			_ = new CacheReadOptions(TimeSpan.FromSeconds(-1));
		}
		catch (Exception e)
		{
			second = e;
		}

		Assert.IsInstanceOfType(first, typeof(ArgumentOutOfRangeException), "an unsupported input must be rejected through the existing argument error layering");
		Assert.IsInstanceOfType(second, typeof(ArgumentOutOfRangeException), "the rejection must be repeatable with the same error layering");
		Assert.AreEqual(((ArgumentOutOfRangeException)first!).ParamName, ((ArgumentOutOfRangeException)second!).ParamName, "the rejection must be stable across repeated attempts");
	}

	[TestMethod]
	public async Task Swr_ReceiptDistinguishes_MemoryFresh_MemoryStale_BackingFresh_BackingStale_AndFactory()
	{
		var clock = FakeClock.Start();
		try
		{
			var memory = new ControllableCacheLayer();
			var backing = new ControllableCacheLayer();
			await using var stack = new CacheStack(null, new(new ICacheLayer[] { memory, backing }));

			//Factory
			var factoryResult = await stack.GetOrSetAsync("factory", _ => Task.FromResult(100), Settings, ReadOptions);
			AssertReceipt(factoryResult, 100, CacheValueOrigin.Factory, -1, "factory");
			Assert.IsFalse(factoryResult.Receipt.IsStale, "[factory] result must not be marked stale");
			Assert.IsFalse(factoryResult.Receipt.IsFromCache, "[factory] result must not be marked from cache");

			//Memory fresh
			var memoryFresh = await stack.GetOrSetAsync("factory", _ => Task.FromResult(999), Settings, ReadOptions);
			AssertReceipt(memoryFresh, 100, CacheValueOrigin.MemoryFresh, 0, "memory fresh");
			Assert.IsFalse(memoryFresh.Receipt.IsStale, "[memory fresh] must not be stale");
			Assert.IsTrue(memoryFresh.Receipt.IsMemoryLayer, "[memory fresh] must identify the memory layer");

			//Backing fresh (clear memory; fresh entry remains in the backing layer)
			await memory.EvictAsync("factory");
			var backingFresh = await stack.GetOrSetAsync("factory", _ => Task.FromResult(999), Settings, ReadOptions);
			AssertReceipt(backingFresh, 100, CacheValueOrigin.BackingFresh, 1, "backing fresh");
			Assert.IsTrue(backingFresh.Receipt.IsBackingLayer, "[backing fresh] must identify a backing layer");
			//Back-population of the fresh backing entry restores the memory layer
			var backPopulated = await WaitUntilAsync(async () =>
			{
				var entry = await memory.GetAsync<int>("factory");
				return entry is not null;
			}, TimeSpan.FromSeconds(5));
			Assert.IsTrue(backPopulated, "[backing fresh] the fresh entry must be back-populated to the memory layer");

			//Memory stale: expire the entry but stay inside the SWR window
			clock.Advance(Ttl + TimeSpan.FromSeconds(1));
			var memoryStale = await stack.GetOrSetAsync("factory", _ => Task.FromResult(101), Settings, ReadOptions);
			AssertReceipt(memoryStale, 100, CacheValueOrigin.MemoryStale, 0, "memory stale");
			Assert.IsTrue(memoryStale.Receipt.IsStale, "[memory stale] must be marked stale");

			//Backing stale: evict memory again while the background refresh is potentially in flight is avoided;
			//use a dedicated key placed only in the backing layer in expired state.
			var backingStaleEntry = new CacheEntry<int>(200, clock.Now + Ttl);
			await backing.SetAsync("backing-stale", backingStaleEntry);
			clock.Advance(Ttl + TimeSpan.FromSeconds(1));
			var backingStale = await stack.GetOrSetAsync("backing-stale", _ => Task.FromResult(201), Settings, ReadOptions);
			AssertReceipt(backingStale, 200, CacheValueOrigin.BackingStale, 1, "backing stale");
			Assert.IsTrue(backingStale.Receipt.IsStale, "[backing stale] must be marked stale");
		}
		finally
		{
			clock.Reset();
		}
	}

	[TestMethod]
	public async Task Swr_ConcurrentHundredReadsOnExpiredEntry_ServeStaleOnceAndTriggerExactlyOneRefresh()
	{
		var clock = FakeClock.Start();
		var layer = new ControllableCacheLayer();
		try
		{
			await using var stack = new CacheStack(null, new(new ICacheLayer[] { layer }));
			var cacheEntry = new CacheEntry<int>(42, clock.Now + Ttl);
			await stack.SetAsync("concurrent-stale", cacheEntry);
			clock.Advance(Ttl + TimeSpan.FromSeconds(1));

			var allReadersAtGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
			var releaseFactory = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
			var factoryCalls = 0;
			var readersInside = 0;

			ValueTask<CacheReadResult<int>> Read()
			{
				return stack.GetOrSetAsync("concurrent-stale", async old =>
				{
					Interlocked.Increment(ref factoryCalls);
					Assert.AreEqual(42, old, "single refresh factory must receive the stale value as old value");
					if (Interlocked.Increment(ref readersInside) == 1)
					{
						allReadersAtGate.TrySetResult(true);
					}
					await releaseFactory.Task;
					return 43;
				}, Settings, ReadOptions);
			}

			var results = new ConcurrentBag<CacheReadResult<int>>();
			var readTasks = Enumerable.Range(0, ConcurrentReaders)
				.Select(async _ => results.Add(await Read()))
				.ToArray();

			var factoryStarted = await Task.WhenAny(allReadersAtGate.Task, Task.Delay(TimeSpan.FromSeconds(5)));
			Assert.AreSame(allReadersAtGate.Task, factoryStarted, "single shared refresh should have started within the fixed window");

			releaseFactory.SetResult(true);
			await Task.WhenAll(readTasks);

			Assert.AreEqual(1, factoryCalls, $"single-flight violated: factory ran {factoryCalls} time(s) for {ConcurrentReaders} concurrent reads");
			Assert.AreEqual(ConcurrentReaders, results.Count, "every concurrent reader must receive a result");

			var staleCount = results.Count(r => r.Receipt is { Origin: CacheValueOrigin.MemoryStale, LayerIndex: 0 });
			Assert.AreEqual(ConcurrentReaders, staleCount, $"all {ConcurrentReaders} reads within the window must receive the stale value 42 with a MemoryStale receipt");
			Assert.IsTrue(results.All(r => r.Value == 42), "every stale read must return the expired value 42 unchanged by the receipt");

			var refreshed = await WaitUntilAsync(async () =>
			{
				var entry = await layer.GetAsync<int>("concurrent-stale");
				return entry is { Value: 43 };
			}, TimeSpan.FromSeconds(5));
			Assert.IsTrue(refreshed, "the single shared refresh must complete and store the new value");

			var afterRefresh = await stack.GetOrSetAsync("concurrent-stale", _ => Task.FromResult(44), Settings, ReadOptions);
			AssertReceipt(afterRefresh, 43, CacheValueOrigin.MemoryFresh, 0, "after refresh");
		}
		finally
		{
			clock.Reset();
		}
	}

	[TestMethod]
	public async Task Swr_WindowEndpoints_ServeStaleAtBoundaryAndWaitPastBoundary()
	{
		//At the exact boundary (expiry + window) the expired value is still served stale.
		using (var clock = FakeClock.Start())
		{
			var memory = new ControllableCacheLayer();
			await using var stack = new CacheStack(null, new(new ICacheLayer[] { memory }));
			var expiry = clock.Now + Ttl;
			await stack.SetAsync("endpoint", new CacheEntry<int>(5, expiry));

			clock.SetTo(expiry + SwrWindow);

			var refreshStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
			var releaseRefresh = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
			var atBoundary = await stack.GetOrSetAsync("endpoint", _ => Task.FromResult(6), Settings, ReadOptions);
			AssertReceipt(atBoundary, 5, CacheValueOrigin.MemoryStale, 0, "at window boundary (inclusive)");

			//Drain the single shared background refresh so it can't bleed into the next stack/layer.
			var refreshed = await WaitUntilAsync(async () =>
			{
				var entry = await memory.GetAsync<int>("endpoint");
				return entry is { Value: 6 };
			}, TimeSpan.FromSeconds(5));
			Assert.IsTrue(refreshed, "the boundary stale serve must still complete its single shared refresh");
			clock.Reset();
		}

		//One tick past the boundary the expired value must not be served: a foreground refresh happens.
		using (var clock = FakeClock.Start())
		{
			var memory = new ControllableCacheLayer();
			await using var stack = new CacheStack(null, new(new ICacheLayer[] { memory }));
			var expiry = clock.Now + Ttl;
			await stack.SetAsync("endpoint", new CacheEntry<int>(5, expiry));

			clock.SetTo(expiry + SwrWindow + TimeSpan.FromSeconds(1));

			var pastBoundary = await stack.GetOrSetAsync("endpoint", old =>
			{
				Assert.AreEqual(5, old, "foreground refresh past the window must still receive the expired value");
				return Task.FromResult(6);
			}, Settings, ReadOptions);
			AssertReceipt(pastBoundary, 6, CacheValueOrigin.Factory, -1, "past window boundary");

			var stored = await memory.GetAsync<int>("endpoint");
			Assert.IsNotNull(stored, "a foreground refresh past the window must persist the new value");
			Assert.AreEqual(6, stored!.Value, "the persisted value must be the factory result");
			clock.Reset();
		}
	}

	[TestMethod]
	public async Task Swr_ForegroundFactoryFailure_SurfacesSameErrorLeavesNoStateAndRetriesStably()
	{
		var clock = FakeClock.Start();
		var layer = new ControllableCacheLayer();
		try
		{
			await using var stack = new CacheStack(null, new(new ICacheLayer[] { layer }));

			var failure = new InvalidOperationException("recoverable factory failure");
			var attempts = 0;
			ValueTask<CacheReadResult<int>> Read() => stack.GetOrSetAsync("foreground-fail", async old =>
			{
				Interlocked.Increment(ref attempts);
				await Task.Yield();
				if (attempts == 1)
				{
					throw failure;
				}
				return 77;
			}, Settings, ReadOptions);

			var first = await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () => await Read());
			Assert.AreSame(failure, first, "the factory failure must propagate through the existing exception model");

			//No successful result may carry cache, counter or temporary state from the failed call.
			Assert.IsNull(await layer.GetAsync<int>("foreground-fail"), "a failed factory must not populate the cache");

			//The identical read immediately retries the factory: the failed single-flight lock must have been released.
			var retry = await Read();
			AssertReceipt(retry, 77, CacheValueOrigin.Factory, -1, "retry after failure");
			Assert.AreEqual(2, attempts, "recovery must invoke the factory exactly one more time");

			//Repeating the same inputs once more is a plain hit with no third factory call.
			var hit = await Read();
			AssertReceipt(hit, 77, CacheValueOrigin.MemoryFresh, 0, "stable hit after recovery");
			Assert.AreEqual(2, attempts, "no temporary lock/counter state may linger from the failed call");
		}
		finally
		{
			clock.Reset();
		}
	}

	[TestMethod]
	public async Task Swr_BackgroundRefreshFailure_DoesNotExtendWindowAndLeavesNoTempState()
	{
		var clock = FakeClock.Start();
		var layer = new ControllableCacheLayer();
		try
		{
			await using var stack = new CacheStack(null, new(new ICacheLayer[] { layer }));
			var originalEntry = new CacheEntry<int>(11, clock.Now + Ttl);
			await stack.SetAsync("stale-fail", originalEntry);
			clock.Advance(Ttl + TimeSpan.FromSeconds(1));

			var attempts = 0;
			ValueTask<CacheReadResult<int>> Read() => stack.GetOrSetAsync("stale-fail", old =>
			{
				var attempt = Interlocked.Increment(ref attempts);
				if (attempt <= 2)
				{
					throw new InvalidOperationException("background refresh failure");
				}
				return Task.FromResult(12);
			}, Settings, ReadOptions);

			//Stale is served and the background refresh fails silently for the caller.
			var stale = await Read();
			AssertReceipt(stale, 11, CacheValueOrigin.MemoryStale, 0, "stale serve before failed refresh");
			var firstAttemptObserved = await WaitUntilAsync(() => Volatile.Read(ref attempts) == 1, TimeSpan.FromSeconds(5));
			Assert.IsTrue(firstAttemptObserved, "the single background refresh must have been attempted");

			//Still inside the window after the failed refresh: the old value remains stale (window untouched).
			var stillStale = await Read();
			AssertReceipt(stillStale, 11, CacheValueOrigin.MemoryStale, 0, "stale serve after failed refresh");
			var cachedAfterFailure = await layer.GetAsync<int>("stale-fail");
			Assert.IsNotNull(cachedAfterFailure, "the failed refresh must not evict or overwrite the stale entry");
			Assert.AreEqual(11, cachedAfterFailure!.Value, "the failed refresh must not change the cached value");
			Assert.AreEqual(originalEntry.Expiry, cachedAfterFailure.Expiry, "a failed refresh must not extend the expiry/stale window");

			//Move just past the SWR window: failure must not have extended it, so reads now wait on a fresh factory result.
			clock.Advance(SwrWindow);
			var recovered = await Read();
			AssertReceipt(recovered, 12, CacheValueOrigin.Factory, -1, "foreground recovery past the unextended window");
			Assert.AreEqual(3, Volatile.Read(ref attempts), "a new refresh must be attempted past the window despite the earlier failures");

			//After recovery the identical read is a stable hit with no further factory invocation.
			var finalAttempts = Volatile.Read(ref attempts);
			var hit = await Read();
			AssertReceipt(hit, 12, CacheValueOrigin.MemoryFresh, 0, "stable fresh hit after recovery");
			Assert.AreEqual(finalAttempts, Volatile.Read(ref attempts), "no extra refresh may occur on a fresh hit");
		}
		finally
		{
			clock.Reset();
		}
	}

	[TestMethod]
	public async Task Swr_EvictRacingWithRefreshCompletion_DoesNotResurrectOldValue()
	{
		var clock = FakeClock.Start();
		var layer = new ControllableCacheLayer();
		try
		{
			await using var stack = new CacheStack(null, new(new ICacheLayer[] { layer }));
			await stack.SetAsync("evict-race", new CacheEntry<int>(31, clock.Now + Ttl));
			clock.Advance(Ttl + TimeSpan.FromSeconds(1));

			var releaseFactory = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
			ValueTask<CacheReadResult<int>> Read() => stack.GetOrSetAsync("evict-race", async old =>
			{
				Assert.AreEqual(31, old, "refresh factory must start from the stale value");
				await releaseFactory.Task;
				return 32;
			}, Settings, ReadOptions);

			//Stale serve starts a single shared refresh held inside the factory.
			var stale = await Read();
			AssertReceipt(stale, 31, CacheValueOrigin.MemoryStale, 0, "stale serve during refresh");

			//The caller deletes the key while the refresh is parked; releasing the factory completes the refresh.
			await stack.EvictAsync("evict-race");
			Assert.IsNull(await layer.GetAsync<int>("evict-race"), "eviction must remove the stale value immediately");
			releaseFactory.SetResult(true);

			var refreshed = await WaitUntilAsync(() => layer.SetCount >= 2, TimeSpan.FromSeconds(5));
			Assert.IsTrue(refreshed, "the in-flight refresh must reach completion after eviction");

			var remaining = await layer.GetAsync<int>("evict-race");
			if (remaining is not null)
			{
				//The only value a racing completion may leave is the freshly generated value - never the old stale value.
				Assert.AreNotEqual(31, remaining.Value, "the delete/refresh race must never resurrect the old value");
				Assert.AreEqual(32, remaining.Value, "if the refresh wins the race, only the refreshed value may remain");
			}

			//Either way, an explicit eviction followed by a read must not bring the deleted entry back.
			await stack.EvictAsync("evict-race");
			clock.Advance(TimeSpan.FromSeconds(1));
			var afterEviction = await stack.GetAsync<int>("evict-race");
			Assert.IsNull(afterEviction, "no stale background traffic may resurrect an evicted entry");
		}
		finally
		{
			clock.Reset();
		}
	}

	[TestMethod]
	public async Task Swr_CorruptBackingLayer_IsSkippedPerErrorLayering_AndUnrecognizedFailurePropagatesRepeatably()
	{
		var clock = FakeClock.Start();
		try
		{
			//A corrupt backing layer that throws the existing serialization error must be skipped, not surface to the caller.
			var corrupt = new ControllableCacheLayer
			{
				GetException = new CacheSerializationException("simulated corrupt backing layer")
			};
			await using var stackWithCorruptBacking = new CacheStack(null, new(new ICacheLayer[] { new MemoryCacheLayer(), corrupt }));

			var fromFactory = await stackWithCorruptBacking.GetOrSetAsync("corrupt", _ => Task.FromResult(88), Settings, ReadOptions);
			AssertReceipt(fromFactory, 88, CacheValueOrigin.Factory, -1, "factory when all layers miss/fail to deserialize");

			//A second read serves the value now cached in the memory layer without touching the factory.
			var factoryCalls = 0;
			var fromMemory = await stackWithCorruptBacking.GetOrSetAsync("corrupt", _ =>
			{
				Interlocked.Increment(ref factoryCalls);
				return Task.FromResult(89);
			}, Settings, ReadOptions);
			AssertReceipt(fromMemory, 88, CacheValueOrigin.MemoryFresh, 0, "memory hit despite corrupt backing layer");
			Assert.AreEqual(0, factoryCalls, "the factory must not run on a memory layer hit");
		}
		finally
		{
			clock.Reset();
		}

		//An unrecognized backing layer failure is not swallowed: it propagates through the existing exception model, repeatedly.
		var unknownFailure = new ArithmeticException("unsupported backing layer failure");
		var brokenLayer = new ControllableCacheLayer { GetException = unknownFailure };
		await using var brokenStack = new CacheStack(null, new(new ICacheLayer[] { brokenLayer }));

		async Task Act() => await brokenStack.GetOrSetAsync("broken", _ => Task.FromResult(1), Settings, ReadOptions);
		var first = await Assert.ThrowsExceptionAsync<ArithmeticException>(Act);
		var second = await Assert.ThrowsExceptionAsync<ArithmeticException>(Act);
		Assert.AreSame(unknownFailure, first, "an unrecognized backing layer failure must propagate as-is");
		Assert.AreSame(unknownFailure, second, "the unrecognized failure must be reproducible with identical inputs");
		Assert.AreEqual(0, brokenLayer.SetCount, "a read that fails to inspect layers must not write through the stack");
	}

	[TestMethod]
	public async Task Swr_CallerCancellationWhileWaiting_DoesNotCancelSharedRefresh()
	{
		var clock = FakeClock.Start();
		var layer = new ControllableCacheLayer();
		try
		{
			await using var stack = new CacheStack(null, new(new ICacheLayer[] { layer }));

			var refreshStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
			var releaseRefresh = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
			var factoryCalls = 0;
			ValueTask<CacheReadResult<int>> Read(CancellationToken token) => stack.GetOrSetAsync("cancel", async old =>
			{
				Interlocked.Increment(ref factoryCalls);
				refreshStarted.TrySetResult(true);
				await releaseRefresh.Task;
				return 64;
			}, Settings, new CacheReadOptions(SwrWindow) { CancellationToken = token });

			//Owner starts the shared refresh and cancels its own wait.
			using var ownerCts = new CancellationTokenSource();
			var ownerTask = Read(ownerCts.Token).AsTask();
			Assert.IsTrue(await Task.WhenAny(refreshStarted.Task, Task.Delay(TimeSpan.FromSeconds(5))) == refreshStarted.Task, "shared refresh should start");
			ownerCts.Cancel();
			await Assert.ThrowsExceptionAsync<TaskCanceledException>(async () => await ownerTask);

			//A second caller joins the still-running shared refresh with its own token and cancels too.
			using var waiterCts = new CancellationTokenSource();
			var waiterTask = Read(waiterCts.Token).AsTask();
			await Task.Yield();
			waiterCts.Cancel();
			await Assert.ThrowsExceptionAsync<TaskCanceledException>(async () => await waiterTask);

			Assert.AreEqual(1, factoryCalls, "cancellation of callers must not cancel (or restart) the shared refresh");

			//Completing the shared refresh stores the value; a later uncancelled read gets it fresh.
			releaseRefresh.SetResult(true);
			var stored = await WaitUntilAsync(async () => (await layer.GetAsync<int>("cancel")) is { Value: 64 }, TimeSpan.FromSeconds(5));
			Assert.IsTrue(stored, "the shared refresh must complete after all callers cancelled");

			var finalRead = await Read(CancellationToken.None);
			AssertReceipt(finalRead, 64, CacheValueOrigin.MemoryFresh, 0, "fresh hit after cancelled waiters");
			Assert.AreEqual(1, factoryCalls, "no second refresh may be triggered once the shared refresh completed");
		}
		finally
		{
			clock.Reset();
		}
	}
}
