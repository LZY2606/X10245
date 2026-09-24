using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace CacheTower.Internal
{
	internal static class DateTimeProvider
	{
		private static DateTime CurrentTime = DateTime.UtcNow;

		/// <summary>
		/// A test-only override for the clock backing <see cref="Now"/>. When <see langword="null"/> (the default and only value in production),
		/// <see cref="Now"/> uses the timer-updated UTC time.
		/// </summary>
		/// <remarks>
		/// This hook exists so tests can use a fake clock. Every expiry, staleness and stale-while-revalidate window calculation
		/// reads <see cref="Now"/>, so overriding the clock exercises the exact same code paths that production uses - no logic is duplicated for tests.
		/// </remarks>
		internal static Func<DateTime>? NowOverride;

		/// <summary>
		/// The current <see cref="DateTime.UtcNow"/>, updated every second.
		/// </summary>
		public static DateTime Now => NowOverride?.Invoke() ?? CurrentTime;

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

		[System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "IDE0052:Remove unread private members", Justification = "Establishes timer and prevents it being garbage collected")]
		private static readonly Timer DateTimeTimer = new(state => UpdateTime(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
	}
}
