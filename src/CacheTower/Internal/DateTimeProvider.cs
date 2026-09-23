using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace CacheTower.Internal
{
	internal static class DateTimeProvider
	{
		private static DateTime? frozenTime;
		private static DateTime realTime = DateTime.UtcNow;

		/// <summary>
		/// The current <see cref="DateTime.UtcNow"/>, updated every second.
		/// </summary>
		/// <remarks>
		/// When a fake clock has been set via <see cref="SetFrozenTime"/>, returns that time instead and is unaffected by the internal timer.
		/// </remarks>
		public static DateTime Now => frozenTime ?? realTime;

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
			if (frozenTime == null)
			{
				realTime = DateTime.UtcNow;
			}
		}

		/// <summary>
		/// Pins <see cref="Now"/> to the supplied <paramref name="time"/>, pausing the internal timer from advancing it.
		/// Pass <see langword="null"/> to resume real time. Intended for deterministic unit testing with a fake clock.
		/// </summary>
		internal static void SetFrozenTime(DateTime? time) => frozenTime = time;

		[System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "IDE0052:Remove unread private members", Justification = "Establishes timer and prevents it being garbage collected")]
		private static readonly Timer DateTimeTimer = new(state => UpdateTime(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
	}
}
