using System;
using System.Threading;

namespace CacheTower.Internal
{
	internal static class DateTimeProvider
	{
		private static readonly object TimeLock = new();
		private static DateTime? frozenTime;

		/// <summary>
		/// The current <see cref="DateTime.UtcNow"/>, updated every second unless a test freezes it.
		/// </summary>
		public static DateTime Now
		{
			get
			{
				lock (TimeLock)
				{
					return frozenTime ?? DateTime.UtcNow;
				}
			}
		}

		/// <summary>
		/// Updates <see cref="Now"/> to the current <see cref="DateTime.UtcNow"/> value. This is automatically called by a timer every second.
		/// </summary>
		public static void UpdateTime()
		{
			lock (TimeLock)
			{
				if (frozenTime == null)
				{
					// Touch the lock so reads observe the most recent publication on runtimes without a volatile field.
					Monitor.PulseAll(TimeLock);
				}
			}
		}

		internal static void SetTime(DateTime value)
		{
			lock (TimeLock)
			{
				frozenTime = value.ToUniversalTime();
			}
		}

		internal static void ResetTime()
		{
			lock (TimeLock)
			{
				frozenTime = null;
			}
		}

		private static readonly Timer DateTimeTimer = new(state => UpdateTime(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
	}
}
