using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CacheTower.Tests.Utils;

/// <summary>
/// Assertions for contract-style tests. Every failure reports the first violating position
/// together with the surrounding context instead of a bare true/false.
/// </summary>
internal static class ContractAssert
{
	/// <summary>
	/// Asserts every receipt equals the expected receipt, reporting the first violation's index and content.
	/// </summary>
	public static void AllReceiptsEqual(IReadOnlyList<CacheReadReceipt> actual, CacheReadReceipt expected, string context)
	{
		for (var i = 0; i < actual.Count; i++)
		{
			if (!actual[i].Equals(expected))
			{
				Assert.Fail($"First receipt violation at index {i} of {actual.Count} ({context}). Expected: {expected}. Actual: {actual[i]}.");
			}
		}
	}

	/// <summary>
	/// Asserts two sequences are equal element-by-element, reporting the first differing index and both lengths.
	/// </summary>
	public static void SequenceEqual<T>(IReadOnlyList<T> expected, IReadOnlyList<T> actual, string context)
	{
		var sharedLength = Math.Min(expected.Count, actual.Count);
		for (var i = 0; i < sharedLength; i++)
		{
			if (!EqualityComparer<T>.Default.Equals(expected[i], actual[i]))
			{
				Assert.Fail($"First sequence violation at index {i} ({context}). Expected: {expected[i]}. Actual: {actual[i]}. Expected length: {expected.Count}. Actual length: {actual.Count}.");
			}
		}

		Assert.AreEqual(expected.Count, actual.Count, $"Sequence length mismatch ({context}).");
	}

	/// <summary>
	/// Waits until the condition holds, failing with a descriptive message when it does not happen in time.
	/// Used to observe fire-and-forget background work without arbitrary sleeps.
	/// </summary>
	public static async Task WaitForAsync(Func<bool> condition, string description, int timeoutMilliseconds = 10000)
	{
		var stopwatch = Stopwatch.StartNew();
		while (!condition())
		{
			if (stopwatch.ElapsedMilliseconds > timeoutMilliseconds)
			{
				Assert.Fail($"Timed out after {timeoutMilliseconds}ms waiting for: {description}.");
			}

			await Task.Delay(5).ConfigureAwait(false);
		}
	}
}
