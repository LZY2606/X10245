using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CacheTower.Tests.StaleWhileRevalidate
{
	/// <summary>
	/// Assertion helpers for the stale-while-revalidate regression materials.
	/// Failures include the contract point, the expected and actual state and the observed layer event log
	/// so the first violation can be diagnosed from the test output rather than just a true/false result.
	/// </summary>
	internal static class ReceiptAssertions
	{
		public static void ShouldBeReceipt(
			this CacheReadReceipt receipt,
			CacheValueSource source,
			CacheValueFreshness freshness,
			int layerIndex,
			string contractPoint,
			IReadOnlyList<ControllableCacheLayer.LayerEvent>? eventLog = null)
		{
			if (receipt.Source != source || receipt.Freshness != freshness || receipt.LayerIndex != layerIndex)
			{
				throw new AssertFailedException(FormatFailure(contractPoint,
					$"Source={source}, Freshness={freshness}, LayerIndex={layerIndex}",
					$"Source={receipt.Source}, Freshness={receipt.Freshness}, LayerIndex={receipt.LayerIndex}",
					eventLog));
			}
		}

		public static void ShouldBeValue<T>(this CacheGetResult<T> result, T expectedValue, string contractPoint,
			IReadOnlyList<ControllableCacheLayer.LayerEvent>? eventLog = null)
		{
			if (!EqualityComparer<T>.Default.Equals(result.Value, expectedValue))
			{
				throw new AssertFailedException(FormatFailure(contractPoint,
					$"Value={expectedValue}", $"Value={result.Value}", eventLog));
			}
		}

		public static void ShouldHaveFactoryCalls(int actual, int expected, string contractPoint,
			IReadOnlyList<IReadOnlyList<ControllableCacheLayer.LayerEvent>>? layerLogs = null)
		{
			if (actual != expected)
			{
				throw new AssertFailedException(FormatFailure(contractPoint,
					$"factory call count={expected}", $"factory call count={actual}",
					layerLogs?.SelectMany(log => log).ToList()));
			}
		}

		public static string FormatFailure(
			string contractPoint,
			string expected,
			string actual,
			IReadOnlyList<ControllableCacheLayer.LayerEvent>? eventLog)
		{
			var builder = new StringBuilder();
			builder.Append("Contract violation at [").Append(contractPoint).AppendLine("]");
			builder.Append("  expected: ").AppendLine(expected);
			builder.Append("  actual:   ").AppendLine(actual);
			if (eventLog is not null && eventLog.Count > 0)
			{
				builder.AppendLine("  layer events (in arrival order):");
				foreach (var layerEvent in eventLog)
				{
					builder.Append("    layer[").Append(layerEvent.LayerIndex)
						.Append("] ").Append(layerEvent.Type)
						.Append(" key='").Append(layerEvent.Key).AppendLine("'");
				}
			}
			return builder.ToString();
		}

		public static List<ControllableCacheLayer.LayerEvent> CombineLogs(params ControllableCacheLayer[] layers)
		{
			var combined = new List<ControllableCacheLayer.LayerEvent>();
			foreach (var layer in layers)
			{
				combined.AddRange(layer.SnapshotEvents());
			}
			return combined.OrderBy(ev => ev.LayerIndex).ToList();
		}
	}
}
