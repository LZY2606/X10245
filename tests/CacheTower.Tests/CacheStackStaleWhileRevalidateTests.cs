using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CacheTower.Tests.Utils;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CacheTower.Tests
{
	/// <summary>
	/// Regression materials for "CacheTower: stale-while-revalidate, single-flight refresh and source receipts".
	/// Every test enters through the public <see cref="CacheStack"/> entry points with a fake clock and controllable cache layers.
	/// </summary>
	[TestClass]
	public class CacheStackStaleWhileRevalidateTests : TestBase
	{
		private static readonly DateTime TimeZero = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

		private static CacheSettings SwrSettings =>
			new(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(30))
			{
				StaleWhileRevalidateWindow = TimeSpan.FromSeconds(300)
			};

		/// <summary>
		/// Waits for an invariant with a fixed timeout. Failure messages keep the condition context so the first violation is locatable.
		/// </summary>
		private static async Task WaitUntilAsync(Func<bool> condition, string context)
		{
			for (var attempt = 1; attempt <= 200; attempt++)
			{
				if (condition())
				{
					return;
				}

				await Task.Delay(25);
			}

			Assert.Fail($"Condition was not met within the timeout. Context: {context}");
		}

		private static void AssertEveryResult<T>(IReadOnlyList<CacheReadResult<T>> results, T expectedValue, string context)
		{
			for (var i = 0; i < results.Count; i++)
			{
				Assert.AreEqual(expectedValue, results[i].Value, $"First value violation at reader #{i}. Context: {context}");
			}
		}

		[TestMethod]
		public async Task GetOrSetWithReceipt_Miss_ReportsFactoryAndPersists()
		{
			using var clock = new FakeClock(TimeZero);
			var layer = new ControllableCacheLayer();
			await using var cacheStack = new CacheStack(null, new(new ICacheLayer[] { layer }));

			var factoryCalls = 0;
			var result = await cacheStack.GetOrSetWithReceiptAsync<int>("key", async old =>
			{
				Interlocked.Increment(ref factoryCalls);
				await Task.Yield();
				return 42;
			}, SwrSettings);

			Assert.AreEqual(42, result.Value, "A miss must return the factory value");
			Assert.AreEqual(CacheValueSource.Factory, result.Source, "A miss must carry a Factory receipt");
			Assert.AreEqual(1, factoryCalls, "The factory must be invoked once");
			Assert.AreEqual(1, layer.SetCount, "The successful refresh must be persisted once");
			var cached = await layer.GetAsync<int>("key");
			Assert.AreEqual(42, cached.Value, "The layer must contain the refreshed value");
			Assert.AreEqual(TimeZero.AddSeconds(60), cached.Expiry, "The new entry must use the settings' TTL against the fake clock");
		}

		[TestMethod]
		public async Task GetOrSetWithReceipt_FreshMemoryHit_ReportsMemoryLayer()
		{
			using var clock = new FakeClock(TimeZero);
			var layer = new ControllableCacheLayer();
			await using var cacheStack = new CacheStack(null, new(new ICacheLayer[] { layer }));
			await cacheStack.SetAsync("key", new CacheEntry<int>(42, TimeZero.AddMinutes(5)));

			var factoryCalls = 0;
			var result = await cacheStack.GetOrSetWithReceiptAsync<int>("key", _ =>
			{
				Interlocked.Increment(ref factoryCalls);
				return Task.FromResult(99);
			}, SwrSettings);

			Assert.AreEqual(42, result.Value);
			Assert.AreEqual(CacheValueSource.MemoryLayer, result.Source, "A fresh hit on the first layer must carry the MemoryLayer receipt");
			Assert.AreEqual(0, factoryCalls, "The factory must not be invoked for a fresh hit");
		}

		[TestMethod]
		public async Task GetOrSetWithReceipt_FallbackHit_ReportsFallbackLayer_AndBackPopulates()
		{
			using var clock = new FakeClock(TimeZero);
			var memoryLayer = new ControllableCacheLayer();
			var fallbackLayer = new ControllableCacheLayer();
			await using var cacheStack = new CacheStack(null, new(new ICacheLayer[] { memoryLayer, fallbackLayer }));
			await fallbackLayer.SetAsync("key", new CacheEntry<int>(22, TimeZero.AddMinutes(5)));

			var result = await cacheStack.GetOrSetWithReceiptAsync<int>("key", _ => Task.FromResult(99), SwrSettings);

			Assert.AreEqual(22, result.Value);
			Assert.AreEqual(CacheValueSource.FallbackLayer, result.Source, "A fresh hit on a lower layer must carry the FallbackLayer receipt");

			await WaitUntilAsync(() => memoryLayer.SetCount == 1, "a fallback hit must trigger a background back-population to the memory layer");
			var backPopulated = await memoryLayer.GetAsync<int>("key");
			Assert.AreEqual(22, backPopulated.Value, "The memory layer must be back-populated with the same value");
		}

		[TestMethod]
		public async Task GetOrSetWithReceipt_ConcurrentHundredMisses_SingleFactoryRefresh()
		{
			using var clock = new FakeClock(TimeZero);
			var layer = new ControllableCacheLayer();
			await using var cacheStack = new CacheStack(null, new(new ICacheLayer[] { layer }));

			const int readerCount = 100;
			var factoryCalls = 0;
			var factoryStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
			var factoryGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
			var barrier = new Barrier(readerCount);

			Task<int> Factory(int old)
			{
				Interlocked.Increment(ref factoryCalls);
				factoryStarted.TrySetResult(true);
				return factoryGate.Task;
			}

			var tasks = Enumerable.Range(0, readerCount).Select(_ => Task.Run(async () =>
			{
				barrier.SignalAndWait();
				return await cacheStack.GetOrSetWithReceiptAsync<int>("key", Factory, SwrSettings);
			})).ToArray();

			await Task.WhenAny(factoryStarted.Task, Task.Delay(TimeSpan.FromSeconds(10)));
			Assert.IsTrue(factoryStarted.Task.IsCompleted, "The single factory invocation must start");
			await Task.Delay(300); //Give all readers time to join the single-flight wait before the refresh completes
			factoryGate.SetResult(42);

			var results = await Task.WhenAll(tasks);

			AssertEveryResult(results, 42, "100 concurrent misses of the same key");
			Assert.AreEqual(1, factoryCalls, "A single refresh must be triggered for 100 concurrent misses");
			Assert.AreEqual(1, layer.SetCount, "The refresh must be persisted exactly once");
			var factoryReceipts = results.Count(r => r.Source == CacheValueSource.Factory);
			var freshReceipts = results.Count(r => r.Source == CacheValueSource.Fresh);
			Assert.AreEqual(1, factoryReceipts, $"Exactly one reader must run the factory but found {factoryReceipts}");
			Assert.AreEqual(readerCount - 1, freshReceipts, $"All other readers must wait on the single-flight refresh (Fresh) but found {freshReceipts}");
		}

		[TestMethod]
		public async Task GetOrSetWithReceipt_ConcurrentHundredStaleReads_SingleBackgroundRefresh()
		{
			using var clock = new FakeClock(TimeZero);
			var layer = new ControllableCacheLayer();
			await using var cacheStack = new CacheStack(null, new(new ICacheLayer[] { layer }));
			await cacheStack.SetAsync("key", new CacheEntry<int>(7, TimeZero.AddSeconds(-30)));

			const int readerCount = 100;
			var factoryCalls = 0;
			var barrier = new Barrier(readerCount);

			var tasks = Enumerable.Range(0, readerCount).Select(_ => Task.Run(async () =>
			{
				barrier.SignalAndWait();
				return await cacheStack.GetOrSetWithReceiptAsync<int>("key", async old =>
				{
					Interlocked.Increment(ref factoryCalls);
					await Task.Delay(100);
					return 100;
				}, SwrSettings);
			})).ToArray();

			var results = await Task.WhenAll(tasks);

			AssertEveryResult(results, 7, "100 concurrent stale reads must all get the stale value during the background refresh");
			Assert.AreEqual(readerCount, results.Count(r => r.Source == CacheValueSource.Stale), "Every read inside the stale-while-revalidate window must carry the Stale receipt");

			await WaitUntilAsync(() => factoryCalls == 1 && layer.SetCount == 1, "the single background refresh must complete and persist once");
			var refreshed = await layer.GetAsync<int>("key");
			Assert.AreEqual(100, refreshed.Value, "The background refresh must store the new value");
			Assert.AreEqual(TimeZero.AddSeconds(60), refreshed.Expiry, "The refresh must use a fresh TTL from the fake clock");

			await Task.Delay(300);
			Assert.AreEqual(1, factoryCalls, "After a successful background refresh no further refreshes must run");
		}

		[TestMethod]
		public async Task GetOrSetWithReceipt_RecoverableFactoryFailure_LeavesNoState_RetryStable()
		{
			using var clock = new FakeClock(TimeZero);
			var layer = new ControllableCacheLayer();
			await using var cacheStack = new CacheStack(null, new(new ICacheLayer[] { layer }));

			var factoryCalls = 0;
			var failNext = true;
			Task<int> Factory(int old)
			{
				var callNumber = Interlocked.Increment(ref factoryCalls);
				if (callNumber == 1)
				{
					return Task.FromException<int>(new InvalidOperationException("recoverable failure injected at the midpoint"));
				}

				return Task.FromResult(42);
			}

			var exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () =>
				await cacheStack.GetOrSetWithReceiptAsync<int>("key", Factory, SwrSettings));
			Assert.AreEqual("recoverable failure injected at the midpoint", exception.Message);

			Assert.IsNull(await layer.GetAsync<int>("key"), "A failed refresh must leave no cached value behind");
			Assert.AreEqual(0, layer.SetCount, "A failed refresh must not perform a set on any layer");
			Assert.AreEqual(1, factoryCalls, "The failed factory must have been invoked exactly once");

			var retried = await cacheStack.GetOrSetWithReceiptAsync<int>("key", Factory, SwrSettings);
			Assert.AreEqual(42, retried.Value, "Retrying the same inputs after recovery must return the value");
			Assert.AreEqual(CacheValueSource.Factory, retried.Source, "The retry must be a fresh foreground Factory refresh");
			Assert.AreEqual(2, factoryCalls, "The retry must invoke the factory exactly once");

			var after = await cacheStack.GetOrSetWithReceiptAsync<int>("key", Factory, SwrSettings);
			Assert.AreEqual(42, after.Value);
			Assert.AreEqual(CacheValueSource.MemoryLayer, after.Source, "After recovery the value must be served from the cache with a MemoryLayer receipt");
			Assert.AreEqual(2, factoryCalls, "A successful result must not carry any factory call count left by the failed call");
			Assert.AreEqual(1, layer.SetCount, "A successful result must not carry any set count left by the failed call");
		}

		[TestMethod]
		public async Task GetOrSetWithReceipt_ConcurrentFactoryFailure_AllCallersSeeSameError_NoStateKept()
		{
			using var clock = new FakeClock(TimeZero);
			var layer = new ControllableCacheLayer();
			await using var cacheStack = new CacheStack(null, new(new ICacheLayer[] { layer }));

			const int readerCount = 10;
			var factoryCalls = 0;
			var expectedException = new InvalidOperationException("boom");
			var factoryStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
			var factoryGate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
			var barrier = new Barrier(readerCount);

			Task<int> FailingFactory(int old)
			{
				Interlocked.Increment(ref factoryCalls);
				factoryStarted.TrySetResult(true);
				return factoryGate.Task;
			}

			var tasks = Enumerable.Range(0, readerCount).Select(_ => Task.Run(async () =>
			{
				barrier.SignalAndWait();
				return await cacheStack.GetOrSetWithReceiptAsync<int>("key", FailingFactory, SwrSettings);
			})).ToArray();

			await Task.WhenAny(factoryStarted.Task, Task.Delay(TimeSpan.FromSeconds(10)));
			Assert.IsTrue(factoryStarted.Task.IsCompleted, "The factory must start once");
			await Task.Delay(300); //Let every other reader join the single-flight wait
			factoryGate.SetException(expectedException);

			for (var i = 0; i < tasks.Length; i++)
			{
				var exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => tasks[i]);
				Assert.AreSame(expectedException, exception, $"First violation at reader #{i}: the shared refresh failure must surface to every waiter");
			}

			Assert.AreEqual(1, factoryCalls, "A failing refresh must still be single-flight");
			Assert.AreEqual(0, layer.SetCount, "The failure must not be persisted");
			Assert.IsNull(await layer.GetAsync<int>("key"), "No value must be cached after the failure");

			var retried = await cacheStack.GetOrSetWithReceiptAsync<int>("key", _ => Task.FromResult(42), SwrSettings);
			Assert.AreEqual(CacheValueSource.Factory, retried.Source, "After a shared failure the next read must recover with a foreground factory refresh");
			Assert.AreEqual(42, retried.Value);
		}

		[TestMethod]
		public async Task GetOrSetWithReceipt_BackgroundRefreshFailure_DoesNotExtendStaleWindow()
		{
			using var clock = new FakeClock(TimeZero);
			var layer = new ControllableCacheLayer();
			await using var cacheStack = new CacheStack(null, new(new ICacheLayer[] { layer }));

			var expiry = TimeZero.AddSeconds(-10);
			await cacheStack.SetAsync("key", new CacheEntry<int>(7, expiry));
			var settings = new CacheSettings(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(30))
			{
				StaleWhileRevalidateWindow = TimeSpan.FromSeconds(120)
			};

			var factoryCalls = 0;
			var firstFactoryStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
			var firstFactoryGate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

			Task<int> Factory(int old)
			{
				var callNumber = Interlocked.Increment(ref factoryCalls);
				if (callNumber == 1)
				{
					firstFactoryStarted.TrySetResult(true);
					return firstFactoryGate.Task;
				}

				return Task.FromResult(100);
			}

			var firstRead = await cacheStack.GetOrSetWithReceiptAsync<int>("key", Factory, settings);
			Assert.AreEqual(CacheValueSource.Stale, firstRead.Source, "A read inside the stale-while-revalidate window must return Stale");
			Assert.AreEqual(7, firstRead.Value, "The stale value must be returned as-is");

			await Task.WhenAny(firstFactoryStarted.Task, Task.Delay(TimeSpan.FromSeconds(10)));
			Assert.IsTrue(firstFactoryStarted.Task.IsCompleted, "The background refresh must start once");

			var entryWhileRefreshing = await layer.GetAsync<int>("key");
			Assert.AreEqual(expiry, entryWhileRefreshing.Expiry, "The stale window is measured from the original expiry - a running refresh must not touch it");

			var secondRead = await cacheStack.GetOrSetWithReceiptAsync<int>("key", Factory, settings);
			Assert.AreEqual(CacheValueSource.Stale, secondRead.Source, "A second read while the refresh is in flight must still see Stale");
			Assert.AreEqual(7, secondRead.Value);
			Assert.AreEqual(1, factoryCalls, "No second refresh may run while one is already in flight");

			firstFactoryGate.SetException(new InvalidOperationException("recoverable background failure"));

			//Stale reads keep triggering single-flight attempts; one must pick up after the failed refresh released the lock.
			await WaitUntilAsync(() => factoryCalls >= 2, "the failed background refresh must release the single-flight lock so another read can retry");
			await WaitUntilAsync(async () => (await layer.GetAsync<int>("key"))?.Value == 100, "the recovered refresh must persist the new value");

			var entryAfter = await layer.GetAsync<int>("key");
			Assert.AreEqual(100, entryAfter.Value);
			Assert.AreEqual(TimeZero.AddSeconds(60), entryAfter.Expiry, "The successful refresh must use a fresh TTL - the failed refresh must not have extended the stale window");

			clock.Set(expiry.AddSeconds(120));
			var atWindowEnd = await cacheStack.GetOrSetWithReceiptAsync<int>("key", Factory, settings);
			Assert.AreEqual(100, atWindowEnd.Value, "The refreshed entry must still be fresh at the old window end");
			Assert.AreEqual(CacheValueSource.MemoryLayer, atWindowEnd.Source);
			Assert.AreEqual(2, factoryCalls, "The stale window failing refresh must not cause extra factory calls after recovery");
		}

		[TestMethod]
		public async Task GetOrSetWithReceipt_EvictDuringRefresh_DoesNotResurrectOldValue()
		{
			using var clock = new FakeClock(TimeZero);
			var memoryLayer = new ControllableCacheLayer();
			var fallbackLayer = new ControllableCacheLayer();
			await using var cacheStack = new CacheStack(null, new(new ICacheLayer[] { memoryLayer, fallbackLayer }));

			var factoryCalls = 0;
			var factoryStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
			var factoryGate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

			Task<int> Factory(int old)
			{
				Interlocked.Increment(ref factoryCalls);
				factoryStarted.TrySetResult(true);
				return factoryGate.Task;
			}

			var ownerRead = cacheStack.GetOrSetWithReceiptAsync<int>("key", Factory, SwrSettings).AsTask();
			var waiterRead = cacheStack.GetOrSetWithReceiptAsync<int>("key", Factory, SwrSettings).AsTask();

			await Task.WhenAny(factoryStarted.Task, Task.Delay(TimeSpan.FromSeconds(10)));
			Assert.IsTrue(factoryStarted.Task.IsCompleted, "The factory must start");
			await Task.Delay(200); //Let the second reader join the single-flight wait

			await cacheStack.EvictAsync("key");

			factoryGate.SetResult(42);

			var ownerResult = await ownerRead;
			var waiterResult = await waiterRead;

			Assert.AreEqual(42, ownerResult.Value, "The caller that ran the refresh still receives the new value");
			Assert.AreEqual(CacheValueSource.Factory, ownerResult.Source);
			Assert.AreEqual(42, waiterResult.Value, "A caller waiting on the shared refresh also receives the new value");
			Assert.AreEqual(CacheValueSource.Fresh, waiterResult.Source);

			Assert.IsNull(await memoryLayer.GetAsync<int>("key"), "The memory layer must not resurrect a value evicted while the refresh ran");
			Assert.IsNull(await fallbackLayer.GetAsync<int>("key"), "The fallback layer must not resurrect a value evicted while the refresh ran");
			Assert.AreEqual(1, memoryLayer.EvictCount, "The memory layer must have been evicted exactly once");
			Assert.AreEqual(1, fallbackLayer.EvictCount, "The fallback layer must have been evicted exactly once");

			var afterEviction = await cacheStack.GetOrSetWithReceiptAsync<int>("key", Factory, SwrSettings);
			Assert.AreEqual(CacheValueSource.Factory, afterEviction.Source, "A read after the evicted refresh must miss and run the factory again");
			Assert.AreEqual(2, factoryCalls);
			Assert.AreEqual(42, await (await memoryLayer.GetAsync<int>("key")!).Wait, "placeholder");
		}
