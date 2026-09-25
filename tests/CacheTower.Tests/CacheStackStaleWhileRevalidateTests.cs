using System;
using System.Threading;
using System.Threading.Tasks;
using CacheTower.Tests.Utils;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CacheTower.Tests
{
	/// <summary>
	/// Contract counterexamples for the opt-in stale-while-revalidate reads of <see cref="CacheStack"/>:
	/// expired values served inside a limited window, single-flight refreshes and per-read source receipts.
	/// All scenarios go through the public cache stack entry points, driven by a fake clock
	/// (<see cref="Internal.DateTimeProvider"/>) and controllable cache layers.
	/// </summary>
	[TestClass]
	public class CacheStackStaleWhileRevalidateTests : TestBase
	{
		protected static readonly DateTime FakeStart = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
		protected static readonly TimeSpan TimeToLive = TimeSpan.FromMinutes(10);
		protected static readonly TimeSpan RevalidateWindow = TimeSpan.FromMinutes(5);
		protected const int ConcurrentReaders = 100;

		protected static CacheSettings SwrSettings => new(TimeToLive) { StaleWhileRevalidateWindow = RevalidateWindow };

		[TestInitialize]
		public void FreezeClock()
		{
			ThreadPool.SetMinThreads(ConcurrentReaders + 16, ConcurrentReaders + 16);
			Internal.DateTimeProvider.SetFakeTime(FakeStart);
		}

		[TestCleanup]
		public void ResetClock()
		{
			Internal.DateTimeProvider.ResetFakeTime();
		}

		private static void AssertReceipt(CacheReadReceipt<string> receipt, CacheValueSource expectedSource, string expectedValue, string context)
		{
			if (receipt.Source != expectedSource || receipt.Value != expectedValue)
			{
				Assert.Fail($"Receipt mismatch at {context}: expected ({expectedSource}, \"{expectedValue}\"), actual ({receipt.Source}, \"{receipt.Value}\").");
			}
		}

		[TestMethod]
		public async Task DefaultStrategy_ExpiredEntry_IsNeverServed()
		{
			var layer = new ControllableCacheLayer("memory");
			await using var cacheStack = new CacheStack(null, new(layer));
			const string cacheKey = nameof(DefaultStrategy_ExpiredEntry_IsNeverServed);
			var expiry = FakeStart.Add(TimeToLive);
			await cacheStack.SetAsync(cacheKey, new CacheEntry<string>("old", expiry));

			//One minute past expiry; no stale-while-revalidate window configured (default strategy).
			Internal.DateTimeProvider.SetFakeTime(expiry.AddMinutes(1));

			var factoryCalls = 0;
			var receipt = await cacheStack.GetOrSetWithReceiptAsync(cacheKey, (old) =>
			{
				factoryCalls++;
				return Task.FromResult("new");
			}, new CacheSettings(TimeToLive));

			AssertReceipt(receipt, CacheValueSource.Factory, "new", "default settings, expired entry");
			Assert.AreEqual(1, factoryCalls, "Default strategy must refresh expired entries in the foreground.");

			//The classic entry point keeps the exact same behaviour.
			await cacheStack.SetAsync(cacheKey, new CacheEntry<string>("old-again", expiry));
			var value = await cacheStack.GetOrSetAsync(cacheKey, (old) =>
			{
				factoryCalls++;
				return Task.FromResult("new-again");
			}, new CacheSettings(TimeToLive));

			Assert.AreEqual("new-again", value, "GetOrSetAsync must never serve an expired value.");
			Assert.AreEqual(2, factoryCalls, "GetOrSetAsync must refresh expired entries in the foreground.");
		}

		[TestMethod]
		public async Task StaleWhileRevalidate_ConcurrentHundredReads_ServeStaleValue_TriggerSingleRefresh()
		{
			var layer = new ControllableCacheLayer("memory");
			await using var cacheStack = new CacheStack(null, new(layer));
			const string cacheKey = nameof(StaleWhileRevalidate_ConcurrentHundredReads_ServeStaleValue_TriggerSingleRefresh);
			var expiry = FakeStart.Add(TimeToLive);
			await cacheStack.SetAsync(cacheKey, new CacheEntry<string>("old", expiry));
			Internal.DateTimeProvider.SetFakeTime(expiry.AddMinutes(1)); //Expired, inside the 5 minute window.

			var factory = new ControlledValueFactory((call, old) => "new");
			factory.CloseGate(1);

			var receipts = await RunConcurrentReadsAsync(cacheStack, cacheKey, factory, SwrSettings, ConcurrentReaders);
			await factory.WaitForCallAsync(1);

			for (var i = 0; i < receipts.Length; i++)
			{
				AssertReceipt(receipts[i], CacheValueSource.Stale, "old", $"concurrent stale read #{i}");
			}
			Assert.AreEqual(1, factory.CallCount, "Single-flight: concurrent stale reads trigger exactly one background refresh.");

			//Further reads while the refresh is in flight are still served stale without new refreshes.
			for (var i = 0; i < 10; i++)
			{
				var extraReceipt = await cacheStack.GetOrSetWithReceiptAsync(cacheKey, factory.AsFunc, SwrSettings);
				AssertReceipt(extraReceipt, CacheValueSource.Stale, "old", $"extra read #{i} during the in-flight refresh");
			}
			Assert.AreEqual(1, factory.CallCount, "An in-flight refresh must not be duplicated.");

			factory.OpenGate(1);
			await WaitForConditionAsync(() => layer.SetCount == 2, $"background refresh to write (SetCount={layer.SetCount})");

			var refreshedReceipt = await cacheStack.GetOrSetWithReceiptAsync(cacheKey, factory.AsFunc, SwrSettings);
			AssertReceipt(refreshedReceipt, CacheValueSource.MemoryLayer, "new", "read after the background refresh completed");
			Assert.AreEqual(1, factory.CallCount, "A fresh entry must not trigger further refreshes.");
		}

		[TestMethod]
		public async Task CacheMiss_ConcurrentHundredReads_ShareSingleFactoryCall()
		{
			var layer = new ControllableCacheLayer("memory");
			await using var cacheStack = new CacheStack(null, new(layer));
			const string cacheKey = nameof(CacheMiss_ConcurrentHundredReads_ShareSingleFactoryCall);

			var factory = new ControlledValueFactory((call, old) => "shared-value");
			factory.CloseGate(1);

			var receiptsTask = RunConcurrentReadsAsync(cacheStack, cacheKey, factory, SwrSettings, ConcurrentReaders);

			//Every reader does exactly two layer lookups before being blocked by the single-flight refresh:
			//one for the initial read and one inside the refresh path. Reaching 2*N lookups proves all
			//readers are positioned before the refresh is allowed to complete.
			await WaitForConditionAsync(() => layer.GetCount >= 2 * ConcurrentReaders,
				$"all {ConcurrentReaders} readers to reach the single-flight refresh (GetCount={layer.GetCount})");
			Assert.AreEqual(2 * ConcurrentReaders, layer.GetCount, "Unexpected extra layer lookups before the refresh completed.");

			factory.OpenGate(1);
			var receipts = await receiptsTask;

			var factoryReceipts = 0;
			var freshReceipts = 0;
			for (var i = 0; i < receipts.Length; i++)
			{
				if (receipts[i].Value != "shared-value")
				{
					Assert.Fail($"First violating receipt at index {i}: expected value \"shared-value\", actual \"{receipts[i].Value}\".");
				}

				switch (receipts[i].Source)
				{
					case CacheValueSource.Factory:
						factoryReceipts++;
						break;
					case CacheValueSource.Fresh:
						freshReceipts++;
						break;
					default:
						Assert.Fail($"First violating receipt at index {i}: unexpected source {receipts[i].Source} (expected Factory or Fresh).");
						break;
				}
			}

			Assert.AreEqual(1, factoryReceipts, "Exactly one reader owns the refresh.");
			Assert.AreEqual(ConcurrentReaders - 1, freshReceipts, "All other readers share the single-flight refresh.");
			Assert.AreEqual(1, factory.CallCount, "Single-flight: exactly one factory call.");
			Assert.AreEqual(1, layer.SetCount, "Single-flight: exactly one cache write.");
		}

		[DataTestMethod]
		[DataRow(-1, CacheValueSource.MemoryLayer, "old")]
		[DataRow(0, CacheValueSource.Stale, "old")]
		[DataRow(299, CacheValueSource.Stale, "old")]
		[DataRow(300, CacheValueSource.Factory, "new")]
		[DataRow(301, CacheValueSource.Factory, "new")]
		public async Task StaleWhileRevalidate_WindowEndpoints(int offsetSeconds, CacheValueSource expectedSource, string expectedValue)
		{
			var layer = new ControllableCacheLayer("memory");
			await using var cacheStack = new CacheStack(null, new(layer));
			var cacheKey = $"{nameof(StaleWhileRevalidate_WindowEndpoints)}_{offsetSeconds}";
			var expiry = FakeStart.Add(TimeToLive);
			await cacheStack.SetAsync(cacheKey, new CacheEntry<string>("old", expiry));
			Internal.DateTimeProvider.SetFakeTime(expiry.AddSeconds(offsetSeconds));

			var factory = new ControlledValueFactory((call, old) => "new");
			if (expectedSource == CacheValueSource.Stale)
			{
				factory.CloseGate(1); //Keep the background refresh in flight so it cannot race the assertions.
			}

			var receipt = await cacheStack.GetOrSetWithReceiptAsync(cacheKey, factory.AsFunc, SwrSettings);
			AssertReceipt(receipt, expectedSource, expectedValue, $"offset {offsetSeconds}s from expiry");

			if (expectedSource == CacheValueSource.MemoryLayer)
			{
				Assert.AreEqual(0, factory.CallCount, "An unexpired entry must not trigger the factory.");
			}
			else if (expectedSource == CacheValueSource.Stale)
			{
				await factory.WaitForCallAsync(1);
				Assert.AreEqual(1, factory.CallCount, "A stale read triggers exactly one background refresh.");
				factory.OpenGate(1);
				await WaitForConditionAsync(() => layer.SetCount == 2, $"background refresh to write (SetCount={layer.SetCount})");
			}
			else
			{
				Assert.AreEqual(1, factory.CallCount, "A read past the window refreshes in the foreground.");
			}
		}

		[TestMethod]
		public async Task FactoryFailure_OnMiss_PropagatesAndLeavesNoResidualState()
		{
			var layer = new ControllableCacheLayer("memory");
			await using var cacheStack = new CacheStack(null, new(layer));
			const string cacheKey = nameof(FactoryFailure_OnMiss_PropagatesAndLeavesNoResidualState);

			var factory = new ControlledValueFactory((call, old) => call switch
			{
				1 => throw new InvalidOperationException("Injected recoverable failure (call 1)."),
				_ => "recovered"
			});

			await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () =>
				await cacheStack.GetOrSetWithReceiptAsync(cacheKey, factory.AsFunc, SwrSettings));

			Assert.AreEqual(1, factory.CallCount);
			Assert.AreEqual(0, layer.SetCount, "A failed factory call must not write anything.");
			Assert.IsNull(layer.Peek<string>(cacheKey), "A failed factory call must not leave cached state.");

			//Retry the identical input: the recoverable failure is gone and the result is stable.
			var receipt = await cacheStack.GetOrSetWithReceiptAsync(cacheKey, factory.AsFunc, SwrSettings);
			AssertReceipt(receipt, CacheValueSource.Factory, "recovered", "retry after recoverable failure");
			Assert.AreEqual(2, factory.CallCount);
			Assert.AreEqual(1, layer.SetCount);
			Assert.AreEqual("recovered", layer.Peek<string>(cacheKey)!.Value);

			//Concurrent readers after the failure see only the recovered value - no residual locks, counts or temp state.
			var receipts = await RunConcurrentReadsAsync(cacheStack, cacheKey, factory, SwrSettings, 50);
			for (var i = 0; i < receipts.Length; i++)
			{
				AssertReceipt(receipts[i], CacheValueSource.MemoryLayer, "recovered", $"post-recovery concurrent read #{i}");
			}
			Assert.AreEqual(2, factory.CallCount, "No extra factory calls from concurrent reads of a fresh entry.");
		}
