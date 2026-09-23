using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CacheTower.Tests.StaleWhileRevalidate
{
	/// <summary>
	/// Shared helpers for the "CacheTower: stale-while-revalidate, single-flight refresh and provenance receipts"
	/// regression material. Assertion messages include the first point of violation plus the cache layer state,
	/// receipt and operation trace so a failure is actionable rather than a bare true/false.
	/// </summary>
	public abstract class StaleWhileRevalidateTestBase : TestBase
	{
		internal static string Describe(params ControllableCacheLayer[] layers)
		{
			var builder = new StringBuilder();
			foreach (var layer in layers)
			{
				var trace = string.Join(", ", layer.SnapshotOperations().Select(op => op.Name));
				builder.AppendLine($"Layer={layer.Name} Sets={layer.SetCount} Gets={layer.GetCount} Evicts={layer.EvictCount} Trace=[{trace}]");
			}
			return builder.ToString();
		}

		internal static void AssertReceipt(
			CacheReadReceipt receipt,
			CacheValueOrigin expectedOrigin,
			CacheValueFreshness expectedFreshness,
			int? expectedLayerIndex,
			string context)
		{
			if (receipt.Origin != expectedOrigin || receipt.Freshness != expectedFreshness || receipt.LayerIndex != expectedLayerIndex)
			{
				throw new AssertFailedException(
					$"{context}{Environment.NewLine}" +
					$"Expected receipt: {expectedOrigin}/{expectedFreshness}/Layer{(expectedLayerIndex.HasValue ? expectedLayerIndex.Value.ToString() : "none")}{Environment.NewLine}" +
					$"Actual receipt:   {receipt}");
			}
		}

		internal static CacheSettings Settings(TimeSpan ttl, TimeSpan staleAfter, TimeSpan? swr)
			=> new(ttl, staleAfter, swr);
	}
}
