using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CacheTower.Serializers;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CacheTower.Tests.StaleWhileRevalidate
{
	/// <summary>
	/// Regression material for "CacheTower: stale-while-revalidate, single-flight refresh and provenance receipts".
	/// Every case enters through the public <see cref="ICacheStack"/> surface with a fake clock and a controllable
	/// cache layer; internal cache stack objects are never fabricated by the tests.
	/// </summary>
	[TestClass]
	public class StaleWhileRevalidateContractTests : StaleWhileRevalidateTestBase
	{
		private static CacheStack CreateStack(out ControllableCacheLayer memory, out ControllableCacheLayer backup)
		{
			memory = new ControllableCacheLayer("memory");
			backup = new ControllableCacheLayer("backup");
			return new CacheStack(null, new(new ICacheLayer[] { memory, backup }));
		}

		private static CacheStack CreateStack(out ControllableCacheLayer memory)
		{
			memory = new ControllableCacheLayer("memory");
			return new CacheStack(null, new(new ICacheLayer[] { memory }));
		}

		/// <summary>
		/// A factory that counts its invocations and can be parked on a gate.
		/// </summary>
		private sealed class CountingFactory
		{
			private int callCount;
			private readonly Func<int, int> produce;
			private readonly TaskCompletionSource<bool> entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
			private TaskCompletionSource<bool>? release;

			public int CallCount => Volatile.Read(ref callCount);
			public Task Entered => entered.Task;

			public CountingFactory(Func<int, int> produce)
			{
				this.produce = produce;
			}

			public void HoldUntil(TaskCompletionSource<bool> releaseGate) => release = releaseGate;

			public async Task<int> Invoke(int oldValue)
			{
				var newCallCount = Interlocked.Increment(ref callCount);
				entered.TrySetResult(true);
				if (release != null)
				{
					await release.Task.ConfigureAwait(false);
				}
				return produce(oldValue) + (newCallCount - 1) * 1000;
			}
		}

		[TestMethod]
		public async Task SwR_DefaultPolicy_NeverReturnsExpiredValue_StaysCompatible()
		{
			var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
			await using var stack = CreateStack(out var memory);
			using (var clock = new FakeClockScope(t0))
			{
				memory.Seed("k", new CacheEntry<int>(17, t0.AddMinutes(10)));
				var settings = new CacheSettings(TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(5));

				//Expired and no stale-while-revalidate window configured -> foreground factory refresh.
				clock.Advance(TimeSpan.FromMinutes(11));
				var (value, receipt) = await stack.GetOrSetAsync<int>("k", _ => Task.FromResult(27), settings, CancellationToken.None);

				Assert.AreEqual(27, value, "Default policy must not return an expired value. " + Describe(memory));
				AssertReceipt(receipt, CacheValueOrigin.Factory, CacheValueFreshness.Factory, null, "Expired default read");
				Assert.AreEqual(1, memory.SetCount, "Successful factory result must populate the cache. " + Describe(memory));
			}
		}

		[TestMethod]
		public async Task SwR_FreshReadFromMemory_ReturnsFreshMemoryReceipt()
		{
			var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
			await using var stack = CreateStack(out var memory, out var backup);
			using (var clock = new FakeClockScope(t0))
			{
				memory.Seed("k", new CacheEntry<int>(17, t0.AddMinutes(10)));
				var settings = Settings(TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(2));

				var (value, receipt) = await stack.GetOrSetAsync<int>("k", _ => Task.FromResult(27), settings, CancellationToken.None);

				Assert.AreEqual(17, value, "Fresh value must be returned unchanged.");
				AssertReceipt(receipt, CacheValueOrigin.CacheLayer, CacheValueFreshness.Fresh, 0, "Fresh memory read");
				Assert.AreEqual(0, memory.SetCount, "Fresh read must not write the cache. " + Describe(memory, backup));
			}
		}

		[TestMethod]
		public async Task SwR_FreshReadFromBackup_ReturnsFreshBackupReceiptAndBackPopulates()
		{
			var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
			await using var stack = CreateStack(out var memory, out var backup);
			using (var clock = new FakeClockScope(t0))
			{
				backup.Seed("k", new CacheEntry<int>(17, t0.AddMinutes(10)));
				var settings = Settings(TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(2));

				var (value, receipt) = await stack.GetOrSetAsync<int>("k", _ => Task.FromResult(27), settings, CancellationToken.None);

				Assert.AreEqual(17, value);
				AssertReceipt(receipt, CacheValueOrigin.CacheLayer, CacheValueFreshness.Fresh, 1, "Fresh backup read");

				await WaitUntilAsync(() => memory.SetCount == 1, TimeSpan.FromSeconds(5));
				Assert.IsTrue(memory.ContainsEntry("k"), "Fresh backup hit must back-populate the memory layer. " + Describe(memory, backup));
			}
		}

		[TestMethod]
		public async Task SwR_StaleUnexpiredRead_ReturnsStaleReceiptAndTriggersOneBackgroundRefresh()
		{
			var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
			await using var stack = CreateStack(out var memory);
			using (var clock = new FakeClockScope(t0))
			{
				memory.Seed("k", new CacheEntry<int>(17, t0.AddMinutes(10)));
				var settings = Settings(TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(2));
				var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
				var factory = new CountingFactory(_ => 27);
				factory.HoldUntil(release);

				clock.Advance(TimeSpan.FromMinutes(6)); //past staleAfter but before expiry
				var (value, receipt) = await stack.GetOrSetAsync<int>("k", factory.Invoke, settings, CancellationToken.None);

				Assert.AreEqual(17, value, "Stale value is served immediately.");
				AssertReceipt(receipt, CacheValueOrigin.CacheLayer, CacheValueFreshness.Stale, 0, "Stale unexpired read");

				await factory.Entered;
				Assert.AreEqual(1, factory.CallCount, "Exactly one background refresh should start.");

				clock.Advance(TimeSpan.FromMinutes(1)); //refresh finishes at t0+7
				release.SetResult(true);
				await WaitUntilAsync(() => memory.Peek<int>("k")?.Value == 27, TimeSpan.FromSeconds(5));

				var refreshed = memory.Peek<int>("k");
				Assert.AreEqual(t0.AddMinutes(17), refreshed!.Expiry, "Refreshed entry expiry is anchored to refresh completion time.");
			}
		}

		[TestMethod]
		public async Task SwR_ExpiredWithinWindow_ServesStaleWithSingleFlightAcross100ConcurrentReads()
		{
			var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
			await using var stack = CreateStack(out var memory);
			using (var clock = new FakeClockScope(t0))
			{
				memory.Seed("k", new CacheEntry<int>(17, t0.AddMinutes(10)));
				var settings = Settings(TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(3));
				var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
				var factory = new CountingFactory(_ => 27);
				factory.HoldUntil(release);

				clock.Advance(TimeSpan.FromMinutes(11)); //1 minute into the 3 minute SWR window

				var barrier = new Barrier(100);
				var readTasks = Enumerable.Range(0, 100).Select(_ => ReadOnceAtBarrierAsync()).ToArray();

				await factory.Entered;
				await Task.Yield();
				Assert.AreEqual(1, factory.CallCount, "All 100 reads must share a single refresh.");

				var receipts = readTasks.Select(t => t.Result.Receipt).ToList();
				Assert.IsTrue(receipts.All(r => r is { Origin: CacheValueOrigin.CacheLayer, Freshness: CacheValueFreshness.Stale, LayerIndex: 0 }),
					"Every concurrent read must receive an identical stale/memory receipt. Receipts=" + string.Join("|", receipts.Select(r => r.ToString())));

				release.SetResult(true);
				var values = (await Task.WhenAll(readTasks)).Select(r => r.Value).Distinct().ToList();
				CollectionAssert.AreEqual(new List<int> { 17 }, values, "Every caller must see the same stale value.");

				await WaitUntilAsync(() => memory.Peek<int>("k")?.Value == 27, TimeSpan.FromSeconds(5));
				Assert.AreEqual(1, factory.CallCount, "Refresh must still have run exactly once after completion. " + Describe(memory));

				async Task<CacheReadResult<int>> ReadOnceAtBarrierAsync()
				{
					barrier.SignalAndWait();
					return await stack.GetOrSetAsync<int>("k", factory.Invoke, settings, CancellationToken.None);
				}
			}
		}

		[TestMethod]
		public async Task SwR_FailedBackgroundRefresh_DoesNotExtendWindowAndLeavesNoTempState()
		{
			var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
			await using var stack = CreateStack(out var memory);
			using (var clock = new FakeClockScope(t0))
			{
				memory.Seed("k", new CacheEntry<int>(17, t0.AddMinutes(10)));
				var settings = Settings(TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(3));
				var attempt = 0;
				var failureObserved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

				//First refresh is a recoverable failure injected mid-scenario; second succeeds.
				Task<int> FailingThenRecoveringFactory(int _)
				{
					var n = Interlocked.Increment(ref attempt);
					if (n == 1)
					{
						failureObserved.TrySetResult(true);
						throw new InvalidOperationException("recoverable refresh failure");
					}
					return Task.FromResult(77);
				}

				clock.Advance(TimeSpan.FromMinutes(11)); //inside window
				var (staleValue, staleReceipt) = await stack.GetOrSetAsync<int>("k", FailingThenRecoveringFactory, settings, CancellationToken.None);
				Assert.AreEqual(17, staleValue);
				Assert.IsTrue(staleReceipt.IsStale, "Failed-attempt read is still stale-served.");

				await failureObserved.Task;
				await WaitUntilAsync(() => attempt == 1, TimeSpan.FromSeconds(5));
				Assert.AreEqual(17, memory.Peek<int>("k")!.Value, "A failed refresh must not overwrite the cached value. " + Describe(memory));

				//Advance beyond expiry + SWR window: the failed refresh must not have extended it.
				clock.Advance(TimeSpan.FromMinutes(3)); //t0+14, window ended at t0+13
				var (foregroundValue, foregroundReceipt) = await stack.GetOrSetAsync<int>("k", FailingThenRecoveringFactory, settings, CancellationToken.None);
				Assert.AreEqual(77, foregroundValue, "Outside the window a foreground factory refresh is required.");
				AssertReceipt(foregroundReceipt, CacheValueOrigin.Factory, CacheValueFreshness.Factory, null, "Post-window read");
				Assert.AreEqual(2, attempt, "Factory should have been invoked twice (one failed, one success).");
			}
		}

		[TestMethod]
		public async Task SwR_FailedForegroundRefresh_PropagatesErrorModelAndCachesNothing()
		{
			var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
			await using var stack = CreateStack(out var memory);
			using (var clock = new FakeClockScope(t0))
			{
				var settings = Settings(TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(2));
				var expected = new InvalidOperationException("boom");

				Task<int> Factory(int _) => throw expected;

				var actual = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
					() => stack.GetOrSetAsync<int>("k", Factory, settings, CancellationToken.None).AsTask());
				Assert.AreSame(expected, actual, "Foreground factory failure must surface the original exception through the existing error model.");
				Assert.IsFalse(memory.ContainsEntry("k"), "A failed foreground refresh must leave no cached value or temp state. " + Describe(memory));
			}
		}

		[TestMethod]
		public async Task SwR_EvictRacingRefreshCompletion_NeverResurrectsOldValue()
		{
			var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
			await using var stack = CreateStack(out var memory);
			using (var clock = new FakeClockScope(t0))
			{
				memory.Seed("k", new CacheEntry<int>(17, t0.AddMinutes(10)));
				var settings = Settings(TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(3));
				var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
				var factory = new CountingFactory(_ => 27);
				factory.HoldUntil(release);

				clock.Advance(TimeSpan.FromMinutes(14)); //past the SWR window -> foreground refresh
				var refreshTask = stack.GetOrSetAsync<int>("k", factory.Invoke, settings, CancellationToken.None).AsTask();
				await factory.Entered;

				//Eviction races against refresh completion.
				var evictTask = stack.EvictAsync("k");
				release.SetResult(true);
				await evictTask;
				await refreshTask;

				Assert.IsFalse(memory.ContainsEntry("k"),
					"A refresh completing concurrently with eviction must not resurrect the value. " + Describe(memory));
				Assert.AreEqual(1, memory.EvictCount, Describe(memory));
			}
		}

		[TestMethod]
		public async Task SwR_CorruptBackupLayer_IsSkippedAndReceiptIdentifiesServingLayer()
		{
			var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
			await using var stack = CreateStack(out var memory, out var backup);
			using (var clock = new FakeClockScope(t0))
			{
				memory.Seed("k", new CacheEntry<int>(17, t0.AddMinutes(10)));
				backup.ThrowOnGet = true;
				var settings = Settings(TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(2));

				var (value, receipt) = await stack.GetOrSetAsync<int>("k", _ => Task.FromResult(27), settings, CancellationToken.None);

				Assert.AreEqual(17, value, "Memory layer serves when backup is never consulted for a memory hit.");
				AssertReceipt(receipt, CacheValueOrigin.CacheLayer, CacheValueFreshness.Fresh, 0, "Corrupt backup, healthy memory");
			}

			await using var stack2 = CreateStack(out var memory2, out var backup2);
			using (var clock2 = new FakeClockScope(t0))
			{
				backup2.Seed("k", new CacheEntry<int>(55, t0.AddMinutes(10)));
				backup2.ThrowOnGet = false;
				//Corrupt only the memory layer so the stack must fall through to the backup layer.
				var corruptMemory = new ThrowOnGetLayer();
				var corruptStack = new CacheStack(null, new(new ICacheLayer[] { corruptMemory, backup2 }));
				await using (corruptStack)
				{
					var settings = Settings(TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(2));
					var (value, receipt) = await corruptStack.GetOrSetAsync<int>("k", _ => Task.FromResult(27), settings, CancellationToken.None);
					Assert.AreEqual(55, value, "A corrupt upper layer must be skipped via CacheSerializationException, serving backup data.");
					AssertReceipt(receipt, CacheValueOrigin.CacheLayer, CacheValueFreshness.Fresh, 1, "Corrupt memory fallback");
				}
			}
		}

		private sealed class ThrowOnGetLayer : ICacheLayer
		{
			public ValueTask CleanupAsync() => default;
			public ValueTask EvictAsync(string cacheKey) => default;
			public ValueTask FlushAsync() => default;
			public ValueTask<CacheEntry<T>?> GetAsync<T>(string cacheKey)
				=> throw new CacheSerializationException("simulated corrupt layer");
			public ValueTask SetAsync<T>(string cacheKey, CacheEntry<T> cacheEntry) => default;
			public ValueTask<bool> IsAvailableAsync(string cacheKey) => new(true);
		}

		[TestMethod]
		public async Task SwR_WindowEndpoints_BoundaryConditionsAreExact()
		{
			var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
			var expiry = t0.AddMinutes(10);
			var settings = Settings(TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(3));

			//Boundary 1: exactly at expiry -> still considered unexpired (Expiry > now) and served stale while refreshing.
			await using var atExpiry = CreateStack(out var layerAtExpiry);
			using (var clock = new FakeClockScope(t0))
			{
				layerAtExpiry.Seed("k", new CacheEntry<int>(17, expiry));
				clock.Advance(TimeSpan.FromMinutes(10));
				var (_, receipt) = await AtExpiryStackRead(atExpiry, layerAtExpiry, settings);
				AssertReceipt(receipt, CacheValueOrigin.CacheLayer, CacheValueFreshness.Stale, 0, "Exactly at expiry must be stale-served.");
			}

			//Boundary 2: one second past expiry but inside window -> stale-served.
			await using var inside = CreateStack(out var insideLayer);
			using (var clock = new FakeClockScope(t0))
			{
				insideLayer.Seed("k", new CacheEntry<int>(17, expiry));
				clock.Advance(TimeSpan.FromMinutes(10).Add(TimeSpan.FromSeconds(1)));
				var (_, receipt) = await AtExpiryStackRead(inside, insideLayer, settings);
				AssertReceipt(receipt, CacheValueOrigin.CacheLayer, CacheValueFreshness.Stale, 0, "Inside SWR window must be stale-served.");
			}

			//Boundary 3: exactly at expiry + window -> window elapsed, foreground refresh.
			await using var edge = CreateStack(out var edgeLayer);
			using (var clock = new FakeClockScope(t0))
			{
				edgeLayer.Seed("k", new CacheEntry<int>(17, expiry));
				clock.Advance(TimeSpan.FromMinutes(13));
				var (value, receipt) = await edge.GetOrSetAsync<int>("k", _ => Task.FromResult(99), settings, CancellationToken.None);
				Assert.AreEqual(99, value, "Exactly at the SWR deadline must force a foreground refresh.");
				AssertReceipt(receipt, CacheValueOrigin.Factory, CacheValueFreshness.Factory, null, "At SWR deadline");
			}

			//Boundary 4: beyond window -> foreground refresh.
			await using var beyond = CreateStack(out var beyondLayer);
			using (var clock = new FakeClockScope(t0))
			{
				beyondLayer.Seed("k", new CacheEntry<int>(17, expiry));
				clock.Advance(TimeSpan.FromMinutes(14));
				var (value, receipt) = await beyond.GetOrSetAsync<int>("k", _ => Task.FromResult(101), settings, CancellationToken.None);
				Assert.AreEqual(101, value);
				AssertReceipt(receipt, CacheValueOrigin.Factory, CacheValueFreshness.Factory, null, "Past SWR deadline");
			}

			static ValueTask<CacheReadResult<int>> AtExpiryStackRead(CacheStack stack, ControllableCacheLayer layer, CacheSettings settings)
				=> stack.GetOrSetAsync<int>("k", _ => Task.FromResult(27), settings, CancellationToken.None);
		}

		[TestMethod]
		public async Task SwR_CallerCancellation_AbandonsWaitButNeverCancelsSharedRefresh()
		{
			var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
			await using var stack = CreateStack(out var memory);
			using (var clock = new FakeClockScope(t0))
			{
				var settings = Settings(TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(2));
				var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
				var factory = new CountingFactory(_ => 42);
				factory.HoldUntil(release);

				//Cold miss foreground refresh in progress.
				var owner = stack.GetOrSetAsync<int>("k", factory.Invoke, settings, CancellationToken.None).AsTask();
				await factory.Entered;

				//A second caller joins the single-flight wait then cancels.
				using var cts = new CancellationTokenSource();
				var waiterTask = stack.GetOrSetAsync<int>("k", factory.Invoke, settings, cts.Token).AsTask();
				cts.Cancel();

				var canceled = await Assert.ThrowsExceptionAsync<TaskCanceledException>(() => waiterTask);
				Assert.IsTrue(canceled.CancellationToken == cts.Token, "Cancellation must be reported through the caller's token.");

				//The shared refresh completes for everyone else.
				release.SetResult(true);
				var ownerResult = await owner;
				Assert.AreEqual(42, ownerResult.Value, "Owner still receives the value produced by the un-cancelled shared refresh.");
				AssertReceipt(ownerResult.Receipt, CacheValueOrigin.Factory, CacheValueFreshness.Factory, null, "Owner read");

				var cached = memory.Peek<int>("k");
				Assert.IsNotNull(cached, "The shared refresh must still populate the cache after a waiter cancelled. " + Describe(memory));
				Assert.AreEqual(1, factory.CallCount, "Cancellation of a waiter must not trigger a second factory call. " + Describe(memory));
			}
		}

		[TestMethod]
		public async Task SwR_Receipts_StayStableAcrossSchedulingPermutations()
		{
			//Fixed seed: jitter changes only scheduling, never the inputs or expected outcome.
			var seedRuns = new[] { 7, 7, 42, 1337 };
			var expectedSequence = new List<(int Value, CacheValueOrigin Origin, CacheValueFreshness Freshness, int? Layer)>();

			for (var run = 0; run < seedRuns.Length; run++)
			{
				var seed = seedRuns[run];
				var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
				await using var stack = CreateStack(out var memory, out var backup);
				var actualSequence = new List<(int, CacheValueOrigin, CacheValueFreshness, int?)>();
				using (var clock = new FakeClockScope(t0))
				{
					var settings = Settings(TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(3));
					memory.Seed("fresh", new CacheEntry<int>(1, t0.AddMinutes(10)));
					memory.Seed("stale", new CacheEntry<int>(2, t0.AddMinutes(10)));
					backup.Seed("backup", new CacheEntry<int>(3, t0.AddMinutes(10)));
					memory.Seed("expired", new CacheEntry<int>(4, t0.AddMinutes(10)));

					var random = new Random(seed);

					//Read in a seed-derived order with yield scheduling; values are keyed so order changes nothing semantic.
					var keys = new[] { "fresh", "stale", "backup", "expired" };
					var orderedKeys = keys.OrderBy(_ => random.Next()).ToArray();
					foreach (var key in orderedKeys)
					{
						if (key == "stale")
						{
							clock.Advance(TimeSpan.FromMinutes(6));
						}
						else if (key == "expired")
						{
							clock.Advance(TimeSpan.FromMinutes(11));
						}

						var read = await stack.GetOrSetAsync<int>(key, _ => Task.FromResult(900 + (key == "expired" ? 1 : 0)), settings, CancellationToken.None);
						actualSequence.Add((read.Value, read.Receipt.Origin, read.Receipt.Freshness, read.Receipt.LayerIndex));
						await Task.Yield();
					}

					//Normalize by key so order independence is directly comparable.
					var keyedResult = new[] { "fresh", "stale", "backup", "expired" }
						.Select(k =>
						{
							var idx = Array.IndexOf(orderedKeys, k);
							return actualSequence[idx];
						})
						.ToList();

					if (run == 0)
					{
						expectedSequence.AddRange(keyedResult);
						//Assert exact receipts on the first, fixed-seed run.
						AssertReceiptFor("fresh", keyedResult[0], 1, CacheValueOrigin.CacheLayer, CacheValueFreshness.Fresh, 0);
						AssertReceiptFor("stale", keyedResult[1], 2, CacheValueOrigin.CacheLayer, CacheValueFreshness.Stale, 0);
						AssertReceiptFor("backup", keyedResult[2], 3, CacheValueOrigin.CacheLayer, CacheValueFreshness.Fresh, 1);
						AssertReceiptFor("expired", keyedResult[3], 901, CacheValueOrigin.Factory, CacheValueFreshness.Factory, null);
					}
					else
					{
						CollectionAssert.AreEqual(expectedSequence, keyedResult,
							$"Scheduling permutation with seed {seed} changed the read receipts. Seed=7 produced the reference sequence.");
					}
				}
			}

			static void AssertReceiptFor(string key, (int Value, CacheValueOrigin Origin, CacheValueFreshness Freshness, int? Layer) actual,
				int expectedValue, CacheValueOrigin origin, CacheValueFreshness freshness, int? layer)
			{
				if (actual.Value != expectedValue || actual.Origin != origin || actual.Freshness != freshness || actual.Layer != layer)
				{
					throw new AssertFailedException($"Key={key} expected=({expectedValue},{origin}/{freshness}/Layer{layer?.ToString() ?? "none"}) actual={actual}");
				}
			}
		}

		private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
		{
			var deadline = DateTime.UtcNow + timeout;
			while (DateTime.UtcNow < deadline)
			{
				if (condition())
				{
					return;
				}
				await Task.Delay(10);
			}

			if (!condition())
			{
				throw new AssertFailedException("Timed out waiting for expected cache state.");
			}
		}
	}
}
