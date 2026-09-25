using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace CacheTower.Internal
{
	internal static class DateTimeProvider
	{
		private static readonly object TimeLock = new();
		private static DateTime? FrozenTime;

		/// <summary>
		/// The current time used by the cache stack.
		/// </summary>
		/// <remarks>
		/// In production this is <see cref="DateTime.UtcNow"/>, updated every second.
		/// Tests can pin the time with <see cref="SetTime"/>; production code and tests read time through this same property.
		/// </remarks>
		public static DateTime Now
		{
			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			get
			{
				lock (TimeLock)
				{
					return FrozenTime ?? DateTime.UtcNow;
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
			lock (TimeLock)
			{
				if (!FrozenTime.HasValue)
				{
					Monitor.PulseAll(TimeLock);
				}
			}
		}

		/// <summary>
		/// Pins <see cref="Now"/> to a specific <paramref name="time"/>, replacing any previously pinned time.
		/// </summary>
		/// <remarks>
		/// Intended for tests requiring a deterministic fake clock - the same <see cref="Now"/> path is used in production.
		/// </remarks>
		/// <param name="time">The fake current time, interpreted as UTC when its kind is unspecified.</param>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		internal static void SetTime(DateTime time)
		{
			if (time.Kind != DateTimeKind.Utc)
			{
				time = DateTime.SpecifyKind(time, DateTimeKind.Utc);
			}

			lock (TimeLock)
			{
				FrozenTime = time;
			}
		}

		/// <summary>
		/// Advances a pinned clock by <paramref name="elapsed"/>.
		/// </summary>
		/// <param name="elapsed">The time to advance the fake clock by.</param>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		internal static void Advance(TimeSpan elapsed)
		{
			lock (TimeLock)
			{
				FrozenTime = (FrozenTime ?? DateTime.UtcNow) + elapsed;
			}
		}

		/// <summary>
		/// Releases a pinned clock so <see cref="Now"/> follows <see cref="DateTime.UtcNow"/> again.
		/// </summary>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		internal static void Reset()
		{
			lock (TimeLock)
			{
				FrozenTime = null;
			}
		}

		[System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "IDE0052:Remove unread private members", Justification = "Establishes timer and prevents it being garbage collected")]
		private static readonly Timer DateTimeTimer = new(state => UpdateTime(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
	}
}
