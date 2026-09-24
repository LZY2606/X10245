using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace CacheTower.Internal
{
	internal static class DateTimeProvider
	{
		private static readonly object TimeLock = new();

		/// <summary>
		/// The current time used by the cache stack.
		/// By default this is <see cref="DateTime.UtcNow"/> updated every second by an internal timer.
		/// The value can be pinned for the purposes of deterministic testing via <see cref="PinTime"/> or <see cref="AdvanceTo(DateTime)"/>.
		/// </summary>
		public static DateTime Now { get; private set; } = DateTime.UtcNow;

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
				if (!isTimePinned)
				{
					Now = DateTime.UtcNow;
				}
			}
		}

		/// <summary>
		/// Pins <see cref="Now"/> to the current <see cref="DateTime.UtcNow"/> value, stopping the automatic timer updates.
		/// Returns a token which restores the default behaviour on dispose.
		/// </summary>
		/// <remarks>
		/// This is intended for unit tests only - production code always reads the timer-updated value.
		/// </remarks>
		internal static IDisposable PinTime()
		{
			lock (TimeLock)
			{
				isTimePinned = true;
				Now = DateTime.UtcNow;
				return new PinnedTimeScope();
			}
		}

		/// <summary>
		/// Pins <see cref="Now"/> to the specified <paramref name="utcTime"/>, stopping the automatic timer updates.
		/// Returns a token which restores the default behaviour on dispose.
		/// </summary>
		/// <remarks>
		/// This is intended for unit tests only - production code always reads the timer-updated value.
		/// </remarks>
		internal static IDisposable PinTime(DateTime utcTime)
		{
			lock (TimeLock)
			{
				isTimePinned = true;
				Now = utcTime.Kind == DateTimeKind.Utc ? utcTime : DateTime.SpecifyKind(utcTime, DateTimeKind.Utc);
				return new PinnedTimeScope();
			}
		}

		/// <summary>
		/// Moves the pinned clock to the specified <paramref name="utcTime"/>.
		/// If the clock isn't pinned yet, it is first pinned to the current <see cref="DateTime.UtcNow"/>.
		/// </summary>
		/// <remarks>
		/// This is intended for unit tests only - production code always reads the timer-updated value.
		/// </remarks>
		internal static void AdvanceTo(DateTime utcTime)
		{
			lock (TimeLock)
			{
				isTimePinned = true;
				Now = utcTime.Kind == DateTimeKind.Utc ? utcTime : DateTime.SpecifyKind(utcTime, utcTime.Kind);
			}
		}

		/// <summary>
		/// Moves the pinned clock forward by the specified <paramref name="duration"/>.
		/// </summary>
		/// <remarks>
		/// This is intended for unit tests only - production code always reads the timer-updated value.
		/// </remarks>
		internal static void AdvanceBy(TimeSpan duration)
		{
			lock (TimeLock)
			{
				isTimePinned = true;
				Now = Now + duration;
			}
		}

		/// <summary>
		/// Unpins the clock, resuming automatic timer updates and immediately synchronising to <see cref="DateTime.UtcNow"/>.
		/// </summary>
		internal static void UnpinTime()
		{
			lock (TimeLock)
			{
				isTimePinned = false;
				Now = DateTime.UtcNow;
			}
		}

		private static bool isTimePinned;

		private sealed class PinnedTimeScope : IDisposable
		{
			public void Dispose() => UnpinTime();
		}

		[System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "IDE0052:Remove unread private members", Justification = "Establishes timer and prevents it being garbage collected")]
		private static readonly Timer DateTimeTimer = new(state => UpdateTime(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
	}
}
