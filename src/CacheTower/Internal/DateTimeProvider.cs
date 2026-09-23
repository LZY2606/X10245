using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace CacheTower.Internal
{
	internal static class DateTimeProvider
	{
		/// <summary>
		/// The current <see cref="DateTime.UtcNow"/>, updated every second.
		/// </summary>
		public static DateTime Now { get; private set; } = DateTime.UtcNow;

		private static volatile bool IsTimeOverridden;

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
			if (!IsTimeOverridden)
			{
				Now = DateTime.UtcNow;
			}
		}

		/// <summary>
		/// Overrides <see cref="Now"/> with a fixed value, pausing the automatic time updates until <see cref="ResetNow"/> is called.
		/// </summary>
		/// <remarks>
		/// This is a test-only fake clock hook (visible via <c>InternalsVisibleTo</c>).
		/// It drives the exact same <see cref="Now"/> value that the production cache paths read - no parallel time source is introduced.
		/// </remarks>
		internal static void OverrideNow(DateTime now)
		{
			IsTimeOverridden = true;
			Now = now;
		}

		/// <summary>
		/// Removes any time override set by <see cref="OverrideNow"/> and resumes automatic time updates.
		/// </summary>
		internal static void ResetNow()
		{
			IsTimeOverridden = false;
			Now = DateTime.UtcNow;
		}

		[System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "IDE0052:Remove unread private members", Justification = "Establishes timer and prevents it being garbage collected")]
		private static readonly Timer DateTimeTimer = new(state => UpdateTime(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
	}
}
