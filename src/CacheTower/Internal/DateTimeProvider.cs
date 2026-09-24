using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace CacheTower.Internal
{
	internal static class DateTimeProvider
	{
		private static readonly object TimeSync = new();
		private static DateTime TimerNow = DateTime.UtcNow;
		private static DateTime? OverriddenNow;

		/// <summary>
		/// The current <see cref="DateTime.UtcNow"/>, updated every second.
		/// </summary>
		public static DateTime Now
		{
			get
			{
				lock (TimeSync)
				{
					return OverriddenNow ?? TimerNow;
				}
			}
		}

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
		public static void UpdateTime()
		{
			lock (TimeSync)
			{
				TimerNow = DateTime.UtcNow;
			}
		}

		/// <summary>
		/// Overrides <see cref="Now"/> with a fixed value, acting as a fake clock. Pass <see langword="null"/> to return to the timer-driven clock.
		/// </summary>
		/// <remarks>
		/// This is a test-only hook (visible via <c>InternalsVisibleTo</c>) and shares the exact same <see cref="Now"/> read path as production code -
		/// there is no separate clock for tests. It is internal to keep it out of the public API surface.
		/// </remarks>
		internal static void OverrideNow(DateTime? now)
		{
			lock (TimeSync)
			{
				OverriddenNow = now;
			}
		}

		[System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "IDE0052:Remove unread private members", Justification = "Establishes timer and prevents it being garbage collected")]
		private static readonly Timer DateTimeTimer = new(state => UpdateTime(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
	}
}
