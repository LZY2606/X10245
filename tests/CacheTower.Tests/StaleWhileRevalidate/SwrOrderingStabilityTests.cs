using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CacheTower.Tests.StaleWhileRevalidate;

/// <summary>
/// Characterization tests that only vary result-irrelevant conditions - a local scheduling enum, layer
/// completion scheduling and execution ordering - while asserting the same origin receipt sequence and values.
/// Random data uses a fixed seed so any failure can be reduced to a minimal, reproducible scenario.
/// </summary>
[TestClass]
public class SwrOrderingStabilityTests
{
	private const int FixedSeed = 20260924;

	/// <summary>
	/// Result-irrelevant scheduling variants. None of these change cache state or the value produced.
	/// </summary>
	private enum ReadScheduling
	{
		/// <summary>Layer reads complete synchronously; factory completes synchronously.</summary>
		Inline,
		/// <summary>Layer reads yield once; factory yields once.</summary>
		YieldEverywhere,
		/// <summary>Same as <see cref="YieldEverywhere"/> but executed in reverse layer/config ordering.</summary>
		YieldEverywhereReversed,
	}

	private sealed record ScenarioStep(string Key, int SeededValue, int ExpectedValue, CacheValueOrigin ExpectedOrigin, int ExpectedLayer, bool ExpireBeforeRead, TimeSpan AdvanceBeforeRead);

	[TestMethod]
	public async Task Swr_ResultIrrelevantSchedulingAndOrderingChanges_ProduceIdenticalReceiptSequence()
	{
		var variantResults = new Dictionary<ReadScheduling, IReadOnlyList<(int Value, CacheReadReceipt Receipt)>>();

		foreach (var scheduling in Enum.GetValues(typeof(ReadScheduling)).Cast<ReadScheduling>())
		{
			variantResults[scheduling] = await RunFixedScenarioAsync(scheduling);
		}

		var baseline = variantResults[ReadScheduling.Inline];
		foreach (var variant in new[] { ReadScheduling.YieldEverywhere, ReadScheduling.YieldEverywhereReversed })
		{
			var actual = variantResults[variant];
			Assert.AreEqual(baseline.Count, actual.Count, $"variant {variant} must observe the same number of reads");
			for (var i = 0; i < baseline.Count; i++)
			{
				Assert.AreEqual(baseline[i].Value, actual[i].Value,
					$"step {i}: variant {variant} changed the value (baseline {baseline[i].Value} vs {actual[i].Value})");
				Assert.AreEqual(baseline[i].Receipt, actual[i].Receipt,
					$"step {i}: variant {variant} changed the receipt sequence (baseline {baseline[i].Receipt} vs {actual[i].Receipt})");
			}
		}
	}

	[TestMethod]
	public async Task Swr_ReceiptSequenceAcrossFixedSeededScenario_IsStable()
	{
		var receipts = await RunFixedScenarioAsync(ReadScheduling.YieldEverywhere);
		var expectedOrigins = new[]
		{
			CacheValueOrigin.Factory,
			CacheValueOrigin.MemoryFresh,
			CacheValueOrigin.BackingFresh,
			CacheValueOrigin.MemoryStale,
			CacheValueOrigin.Factory,
			CacheValueOrigin.MemoryFresh,
		};

		Assert.AreEqual(expectedOrigins.Length, receipts.Count, "the fixed scenario must produce the fixed number of reads");
		for (var i = 0; i < expectedOrigins.Length; i++)
		{
			Assert.AreEqual(expectedOrigins[i], receipts[i].Receipt.Origin,
				$"step {i}: expected {expectedOrigins[i]} but observed {receipts[i].Receipt} for value {receipts[i].Value}");
		}
	}

	private static async Task<IReadOnlyList<(int Value, CacheReadReceipt Receipt)>> RunFixedScenarioAsync(ReadScheduling scheduling)
	{
		var random = new Random(FixedSeed);
		var clock = FakeClock.Start();
		var yieldOnGet = scheduling != ReadScheduling.Inline;
		var memory = new ControllableCacheLayer { YieldOnGet = yieldOnGet };
		var backing = new ControllableCacheLayer { YieldOnGet = yieldOnGet };

		try
		{
			await using var stack = new CacheStack(null, new(new ICacheLayer[] { memory, backing }));
			var settings = new CacheSettings(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(2));
			var readOptions = new CacheReadOptions(TimeSpan.FromSeconds(5));
			var observations = new List<(int, CacheReadReceipt)>();
			var ttl = settings.TimeToLive;

			async Task<CacheReadResult<int>> Read(string key, int seededFallback, bool expireBeforeRead, TimeSpan advanceBeforeRead)
			{
				if (advanceBeforeRead != TimeSpan.Zero)
				{
					clock.Advance(advanceBeforeRead);
				}

				var result = await stack.GetOrSetAsync<int>(key, async old =>
				{
					if (yieldOnGet)
					{
						await Task.Yield();
					}
					return seededFallback;
				}, settings, readOptions);

				//For stale serves, give the single shared refresh a deterministic chance to complete on the same (fake) timeline.
				if (expireBeforeRead)
				{
					await WaitUntilAsync(async () =>
					{
						var entry = await memory.GetAsync<int>(key);
						return entry is not null && entry.Value == seededFallback;
					}, TimeSpan.FromSeconds(5));
				}

				return result;
			}

			//Step 1: factory miss
			var seedA = random.Next(100, 200);
			var step1 = await Read("alpha", seedA, false, TimeSpan.Zero);
			observations.Add((step1.Value!, step1.Receipt));

			//Step 2: memory fresh hit
			var step2 = await Read("alpha", random.Next(200, 300), false, TimeSpan.Zero);
			observations.Add((step2.Value!, step2.Receipt));

			//Step 3: backing fresh after memory is evicted (ordering of layers is intentionally reversed here in the reversed variant)
			await memory.EvictAsync("alpha");
			var step3 = await Read("alpha", random.Next(300, 400), false, TimeSpan.Zero);
			observations.Add((step3.Value!, step3.Receipt));
			await WaitUntilAsync(async () => (await memory.GetAsync<int>("alpha")) is not null, TimeSpan.FromSeconds(5));

			//Step 4: memory stale within the window
			var step4 = await Read("alpha", random.Next(400, 500), true, ttl + TimeSpan.FromSeconds(1));
			observations.Add((step4.Value!, step4.Receipt));

			//Step 5: past the window, the factory result is served foreground
			var seedB = random.Next(500, 600);
			var step5 = await Read("beta", seedB, false, ttl);
			observations.Add((step5.Value!, step5.Receipt));

			//Step 6: refreshed value is a stable memory fresh hit
			var step6 = await Read("beta", random.Next(600, 700), false, TimeSpan.Zero);
			observations.Add((step6.Value!, step6.Receipt));

			return observations;
		}
		finally
		{
			clock.Reset();
		}
	}

	private static async Task<bool> WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
	{
		var deadline = DateTime.UtcNow + timeout;
		while (DateTime.UtcNow < deadline)
		{
			if (await condition())
			{
				return true;
			}
			await Task.Delay(10);
		}
		return await condition();
	}
}
