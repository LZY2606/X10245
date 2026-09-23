using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace CacheTower.Internal
{
	internal static class DateTimeProvider
	{
		/// <summary>
		/// The current time used by the cache stack. Normally this is <see cref="DateTime.UtcNow"/> and is updated every second by the internal timer.
		/// </summary>
		/// <remarks>
		/// Tests can take control of the clock with <see cref="SetTime"/> and return control with <see cref="ResetToSystemClock"/>.
		/// All production code reads this single property - there is no separate clock implementation for tests.
		/// </remarks>
		public static DateTime Now { get; private set; } = DateTime.UtcNow;

		private static DateTime? FrozenTime;

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
		public static void UpdateTime() => Now = FrozenTime ?? DateTime.UtcNow;

		/// <summary>
		/// Takes manual control of the clock, setting <see cref="Now"/> to <paramref name="now"/>.
		/// Intended for unit tests that need to deterministically move time without real delays.
		/// </summary>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static void SetTime(DateTime now)
		{
			var utcNow = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, now.Second, DateTimeKind.Utc);
			FrozenTime = utcNow;
			Now = utcNow;
		}

		/// <summary>
		/// Advances a manually controlled clock by <paramref name="elapsed"/>.
		/// </summary>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static void Advance(TimeSpan elapsed)
		{
			if (FrozenTime.HasValue)
			{
				SetTime(FrozenTime.Value + elapsed);
			}
			else
			{
				SetTime(DateTime.UtcNow + elapsed);
			}
		}

		/// <summary>
		/// Returns control of the clock to the system timer. Intended for unit tests.
		/// </summary>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static void ResetToSystemClock()
		{
			FrozenTime = null;
			UpdateTime();
		}

		[System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "IDE0052:Remove unread private members", Justification = "Establishes timer and prevents it being garbage collected")]
		private static readonly Timer DateTimeTimer = new(state => UpdateTime(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
	}
}
