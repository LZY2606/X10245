using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CacheTower.Tests.Utils;

/// <summary>
/// Polling helper that keeps tests deterministic without real-time sleeps.
/// Failure messages carry the waited-on condition so the first violated contract includes context.
/// </summary>
internal static class TestTiming
{
	public static async Task WaitUntilAsync(Func<bool> condition, string context, int timeoutMs = 10000)
	{
		var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
		while (true)
		{
			bool result;
			try
			{
				result = condition();
			}
			catch (Exception ex)
			{
				throw new AssertFailedException("Condition for '" + context + "' threw: " + ex);
			}
			if (result)
			{
				return;
			}
			if (DateTime.UtcNow > deadline)
			{
				throw new AssertFailedException($"Timed out after {timeoutMs}ms waiting for: {context}");
			}
			await Task.Delay(10);
		}
	}
}
