using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CacheTower.Tests.StaleWhileRevalidate
{
	/// <summary>
	/// Base class that drives the cache stack with a fake clock via the internal
	/// <see cref="CacheTower.Internal.DateTimeProvider"/> test hook. The hook feeds the exact same
	/// production time path (<see cref="CacheTower.Internal.DateTimeProvider.Now"/>); it is reset after every test.
	/// </summary>
	public abstract class FakeClockTestBase : TestBase
	{
		protected static readonly DateTime ClockEpoch = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

		private static DateTime FakeNow;

		[TestInitialize]
		public void ResetFakeClock() => SetFakeClock(ClockEpoch);

		[TestCleanup]
		public void ClearFakeClock() => CacheTower.Internal.DateTimeProvider.SetTestTimeOverride(null);

		protected static void SetFakeClock(DateTime now)
		{
			FakeNow = now;
			CacheTower.Internal.DateTimeProvider.SetTestTimeOverride(now);
		}

		protected static void AdvanceFakeClock(TimeSpan duration) => SetFakeClock(FakeNow + duration);

		/// <summary>
		/// Waits until <paramref name="condition"/> holds, using the real clock for the timeout.
		/// Fails with the given context (rather than a bare true/false) on timeout.
		/// </summary>
		protected static async Task WaitForConditionAsync(Func<bool> condition, string context)
		{
			var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
			while (!condition())
			{
				if (DateTime.UtcNow > deadline)
				{
					Assert.Fail($"Timed out waiting for condition. Context: {context}");
				}

				await Task.Delay(10);
			}
		}
	}
}
