using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CacheTower.Tests.StaleWhileRevalidate
{
	/// <summary>
	/// Assertion helpers for <see cref="CacheReadResult{T}"/> receipts. Failures always report the
	/// first violating position plus the surrounding context (scenario, seed, ordering) instead of a bare true/false.
	/// </summary>
	public static class ReceiptAssert
	{
		public static string Describe<T>(CacheReadResult<T> result) => $"(Source={result.Source}, Value={result.Value})";

		public static void AreEqual<T>(CacheReadResult<T> expected, CacheReadResult<T> actual, string context)
		{
			if (expected.Source != actual.Source || !EqualityComparer<T>.Default.Equals(expected.Value, actual.Value))
			{
				Assert.Fail($"Receipt mismatch. Context: {context}. Expected: {Describe(expected)}. Actual: {Describe(actual)}.");
			}
		}

		/// <summary>
		/// Asserts every receipt has the expected source and value, reporting the first violating index on failure.
		/// </summary>
		public static void AllAre<T>(IReadOnlyList<CacheReadResult<T>> results, CacheReadSource expectedSource, T expectedValue, string context)
		{
			for (var i = 0; i < results.Count; i++)
			{
				var result = results[i];
				if (result.Source != expectedSource || !EqualityComparer<T>.Default.Equals(result.Value, expectedValue))
				{
					Assert.Fail($"First violation at index {i} of {results.Count}. Context: {context}. Expected: (Source={expectedSource}, Value={expectedValue}). Actual: {Describe(result)}.");
				}
			}
		}

		/// <summary>
		/// Asserts the exact number of receipts with the given source, reporting the full receipt sequence on failure.
		/// </summary>
		public static void CountSources<T>(IReadOnlyList<CacheReadResult<T>> results, CacheReadSource source, int expectedCount, string context)
		{
			var actualCount = 0;
			foreach (var result in results)
			{
				if (result.Source == source)
				{
					actualCount++;
				}
			}

			if (actualCount != expectedCount)
			{
				var sequence = string.Join(", ", System.Linq.Enumerable.Select(results, Describe));
				Assert.Fail($"Source count mismatch. Context: {context}. Expected {expectedCount} receipt(s) with Source={source}, found {actualCount}. Sequence: [{sequence}].");
			}
		}
	}
}
