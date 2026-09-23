using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CacheTower.Providers.Memory;
using CacheTower.Serializers;
using CacheTower.Tests.Utils;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CacheTower.Tests
{
	/// <summary>
	/// Contract tests for the opt-in stale-while-revalidate read strategy
	/// (<see cref="ICacheStack.GetOrSetWithReceiptAsync{T}(string, Func{T, Task{T}}, CacheSettings, CancellationToken)"/>)
	/// covering caching, stale-while-revalidate and single-flight refresh semantics.
	/// </summary>
	[TestClass]
	public class StaleWhileRevalidateTests : TestBase
	{
		private static readonly DateTime ClockStart = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
		private static readonly TimeSpan TimeToLive = TimeSpan.FromMinutes(10);
		private static readonly TimeSpan StaleWindow = TimeSpan.FromMinutes(5);

		private static CacheSettings SwrSettings => new(TimeToLive) { StaleWhileRevalidateWindow = StaleWindow };

		private static async Task WaitForAsync(Func<bool> condition, string context)
		{
			var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
			while (!condition())
			{
				if (DateTime.UtcNow > deadline)
				{
					Assert.Fail($"Timed out waiting for condition: {context}");
				}
				await Task.Delay(10);
			}
		}

		[TestMethod, ExpectedException(typeof(ArgumentNullException))]
		public async Task GetOrSetWithReceipt_ThrowsOnNullKey()
		{
			await using var cacheStack = new CacheStack(null, new(new[] { new MemoryCacheLayer() }));
			await cacheStack.GetOrSetWithReceiptAsync<int>(null, (old) => Task.FromResult(5), SwrSettings);
		}
		[TestMethod, ExpectedException(typeof(ArgumentNullException))]
		public async Task GetOrSetWithReceipt_ThrowsOnNullFactory()
		{
			await using var cacheStack = new CacheStack(null, new(new[] { new MemoryCacheLayer() }));
			await cacheStack.GetOrSetWithReceiptAsync<int>("MyCacheKey", null, SwrSettings);
		}
		[TestMethod, ExpectedException(typeof(ObjectDisposedException))]
		public async Task GetOrSetWithReceipt_ThrowsOnUseAfterDisposal()
		{
			var cacheStack = new CacheStack(null, new(new[] { new MemoryCacheLayer() }));
			await using (cacheStack)
			{ }

			await cacheStack.GetOrSetWithReceiptAsync<int>("KeyDoesntMatter", (old) => Task.FromResult(1), SwrSettings);
		}
		[TestMethod]
		public async Task GetOrSetWithReceipt_PreCancelledToken_LeavesNoState()
		{
			using var clock = new FakeClock(ClockStart);
			var layer = new ControllableCacheLayer("memory");
			await using var cacheStack = new CacheStack(null, new(new ICacheLayer[] { layer }));

			var factoryCalls = 0;
			await Assert.ThrowsExceptionAsync<OperationCanceledException>(() =>
				cacheStack.GetOrSetWithReceiptAsync<int>("GetOrSetWithReceipt_PreCancelledToken", (old) =>
				{
					factoryCalls++;
					return Task.FromResult(1);
				}, SwrSettings, new CancellationToken(true)).AsTask());

			Assert.AreEqual(0, factoryCalls, "A pre-cancelled caller must not invoke the factory");
			Assert.AreEqual(0, layer.SetCallCount, "A pre-cancelled caller must not write to any cache layer");
			Assert.IsNull(await layer.PeekAsync<int>("GetOrSetWithReceipt_PreCancelledToken"), "A pre-cancelled caller must not leave cache state");
		}

		[TestMethod]
		public async Task GetOrSetWithReceipt_CacheMiss_ReportsFactoryThenMemoryLayer()
		{
			using var clock = new FakeClock(ClockStart);
			await using var cacheStack = new CacheStack(null, new(new[] { new MemoryCacheLayer() }));

			var factoryCalls = 0;
			var miss = await cacheStack.GetOrSetWithReceiptAsync<int>("GetOrSetWithReceipt_CacheMiss", (old) =>
			{
				factoryCalls++;
				return Task.FromResult(42);
			}, SwrSettings);

			Assert.AreEqual(CacheValueSource.Factory, miss.Source, "A cache miss should be served by the factory");
			Assert.AreEqual(42, miss.Value);
			Assert.AreEqual(-1, miss.LayerIndex, "A factory-served value has no serving layer");

			var hit = await cacheStack.GetOrSetWithReceiptAsync<int>("GetOrSetWithReceipt_CacheMiss", (old) =>
			{
				factoryCalls++;
				return Task.FromResult(99);
			}, SwrSettings);

			Assert.AreEqual(CacheValueSource.MemoryLayer, hit.Source, "An unexpired hit on the top layer should report the memory layer");
			Assert.AreEqual(42, hit.Value, "The receipt must not change the cached value");
			Assert.AreEqual(0, hit.LayerIndex);
			Assert.AreEqual(1, factoryCalls, "The factory must not run again for an unexpired hit");
		}
		[TestMethod]
		public async Task GetOrSetWithReceipt_FallbackLayerHit_ReportsFallbackLayer()
		{
			using var clock = new FakeClock(ClockStart);
			var memoryLayer = new ControllableCacheLayer("memory");
			var fallbackLayer = new ControllableCacheLayer("fallback");
			await using var cacheStack = new CacheStack(null, new(new ICacheLayer[] { memoryLayer, fallbackLayer }));

			var cacheEntry = new CacheEntry<int>(42, TimeToLive);
			await fallbackLayer.SetAsync("GetOrSetWithReceipt_FallbackLayerHit", cacheEntry);

			var hit = await cacheStack.GetOrSetWithReceiptAsync<int>("GetOrSetWithReceipt_FallbackLayerHit", (old) => Task.FromResult(99), SwrSettings);

			Assert.AreEqual(CacheValueSource.FallbackLayer, hit.Source, "An unexpired hit on a lower layer should report the fallback layer");
			Assert.AreEqual(42, hit.Value);
			Assert.AreEqual(1, hit.LayerIndex);

			//Back-population moves the value to the memory layer in the background
			await WaitForAsync(() => memoryLayer.SetCallCount >= 1, "back-population to write the memory layer");

			var backPopulatedHit = await cacheStack.GetOrSetWithReceiptAsync<int>("GetOrSetWithReceipt_FallbackLayerHit", (old) => Task.FromResult(99), SwrSettings);
			Assert.AreEqual(CacheValueSource.MemoryLayer, backPopulatedHit.Source, "After back-population the memory layer should serve the value");
			Assert.AreEqual(42, backPopulatedHit.Value);
			Assert.AreEqual(0, backPopulatedHit.LayerIndex);
		}

		[TestMethod]
		public async Task GetOrSetWithReceipt_ExpiredWithinWindow_ReturnsStaleAndRefreshesOnce()
		{
			using var clock = new FakeClock(ClockStart);
			var layer = new ControllableCacheLayer("memory");
			await using var cacheStack = new CacheStack(null, new(new ICacheLayer[] { layer }));

			var cacheEntry = await cacheStack.SetAsync("GetOrSetWithReceipt_ExpiredWithinWindow", 17, TimeToLive);
			clock.Set(cacheEntry.Expiry + TimeSpan.FromMinutes(1));

			var factoryCalls = 0;
			Func<int, Task<int>> factory = async (old) =>
			{
				Interlocked.Increment(ref factoryCalls);
				await Task.Delay(50);
				return 27;
			};

			var staleRead = await cacheStack.GetOrSetWithReceiptAsync<int>("GetOrSetWithReceipt_ExpiredWithinWindow", factory, SwrSettings);
			Assert.AreEqual(CacheValueSource.Stale, staleRead.Source, "An expired entry inside the window should be served stale");
			Assert.AreEqual(17, staleRead.Value, "The stale receipt must not change the stale value");
			Assert.AreEqual(0, staleRead.LayerIndex);

			for (var i = 0; i < 10; i++)
			{
				var followUpRead = await cacheStack.GetOrSetWithReceiptAsync<int>("GetOrSetWithReceipt_ExpiredWithinWindow", factory, SwrSettings);
				Assert.AreEqual(CacheValueSource.Stale, followUpRead.Source, $"Read #{i} while the refresh is in flight should stay stale");
				Assert.AreEqual(17, followUpRead.Value, $"Read #{i} while the refresh is in flight should keep the stale value");
			}

			await WaitForAsync(() =>
			{
				var current = layer.PeekAsync<int>("GetOrSetWithReceipt_ExpiredWithinWindow").Result;
				return current != null && current.Value == 27;
			}, "background refresh to cache the new value");

			var refreshedRead = await cacheStack.GetOrSetWithReceiptAsync<int>("GetOrSetWithReceipt_ExpiredWithinWindow", factory, SwrSettings);
			Assert.AreEqual(CacheValueSource.MemoryLayer, refreshedRead.Source, "After the background refresh the memory layer should serve the new value");
			Assert.AreEqual(27, refreshedRead.Value);
			Assert.AreEqual(1, factoryCalls, "Single-flight: one refresh for the whole stale window");
		}
	}
}
