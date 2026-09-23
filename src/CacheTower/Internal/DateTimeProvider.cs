using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace CacheTower.Internal
{
	internal static class DateTimeProvider
	{
		private static DateTime CurrentTime = DateTime.UtcNow;
		private static Func<DateTime>? TimeSourceOverride;

		/// <summary>
		/// The current <see cref="DateTime.UtcNow"/>, updated every second.
		/// </summary>
		public static DateTime Now => TimeSourceOverride?.Invoke() ?? CurrentTime;

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
		/// Overrides the time source used by <see cref="Now"/>. Pass <see langword="null"/> to restore the default behaviour.
		/// </summary>
		/// <remarks>
		/// This is intended for unit tests that require a fake clock. It is not a parallel time source:
		/// all production code paths continue to read <see cref="Now"/>, so overriding the source changes
		/// the exact same clock that <see cref="CacheStack"/>, <see cref="CacheEntry{T}"/> and the cache layers use.
		/// </remarks>
		internal static void OverrideTimeSource(Func<DateTime>? timeSource) => TimeSourceOverride = timeSource;

		[System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "IDE0052:Remove unread private members", Justification = "Establishes timer and prevents it being garbage collected")]
		private static readonly Timer DateTimeTimer = new(state => UpdateTime(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
	}
}
