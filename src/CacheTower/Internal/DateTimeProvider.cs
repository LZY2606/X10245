using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace CacheTower.Internal
{
	internal static class DateTimeProvider
	{
		private static DateTime CurrentTime = DateTime.UtcNow;
		private static DateTime? FakeTime;

		/// <summary>
		/// The current <see cref="DateTime.UtcNow"/>, updated every second.
		/// </summary>
		/// <remarks>
		/// When a fake time has been set via <see cref="SetFakeTime(DateTime)"/> (a test-only hook), that value is returned instead.
		/// This ensures tests exercise the exact same time source as the production path.
		/// </remarks>
		public static DateTime Now => FakeTime ?? CurrentTime;

		/// <summary>
		/// Updates <see cref="Now"/> to the current <see cref="DateTime.UtcNow"/> value. This is automatically called by a timer every second.
		/// </summary>
		/// <remarks>
		/// This is intended to only be triggered by the internal timer or by unit tests that require it.
		/// The reason why tests need it is due to the fast turn around of setting a value and testing the outcome.
		/// Real applications aren't immediately setting a cache value manually, calling <see cref="ICacheStack.GetOrSetAsync{T}(string, Func{T, System.Threading.Tasks.Task{T}}, CacheSettings)"/> and then comparing whether the results are the same.
		/// The alternative for the tests is just "waiting" an extra second between setting a value and retrieving it however that makes the testing slower and the tests more confusing.
		/// </remarks>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static void UpdateTime() => CurrentTime = DateTime.UtcNow;

		/// <summary>
		/// Test-only hook that freezes <see cref="Now"/> at the given <paramref name="fakeTime"/>, acting as a fake clock.
		/// Every consumer of <see cref="Now"/> (cache entries, cache layers and the cache stack) observes the fake time,
		/// so tests go through the same production code paths rather than a parallel implementation.
		/// </summary>
		/// <param name="fakeTime">The time to freeze <see cref="Now"/> at.</param>
		internal static void SetFakeTime(DateTime fakeTime) => FakeTime = fakeTime;

		/// <summary>
		/// Test-only hook that advances the fake clock by the given <paramref name="duration"/>.
		/// If no fake time has been set, the current time is used as the starting point.
		/// </summary>
		/// <param name="duration">How far to advance the fake clock.</param>
		internal static void AdvanceFakeTime(TimeSpan duration) => FakeTime = Now + duration;

		/// <summary>
		/// Test-only hook that removes any fake time, restoring <see cref="Now"/> to the real clock.
		/// </summary>
		internal static void ResetFakeTime() => FakeTime = null;

		[System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "IDE0052:Remove unread private members", Justification = "Establishes timer and prevents it being garbage collected")]
		private static readonly Timer DateTimeTimer = new(state => UpdateTime(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
	}
}
