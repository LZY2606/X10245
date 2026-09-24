using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace CacheTower.Internal
{
	internal static class DateTimeProvider
	{
		private static bool IsTimeOverridden;

		/// <summary>
		/// The current <see cref="DateTime.UtcNow"/>, updated every second.
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
			if (!IsTimeOverridden)
			{
				Now = DateTime.UtcNow;
			}
		}

		/// <summary>
		/// Overrides <see cref="Now"/> with a fixed time, pausing the automatic timer updates.
		/// This shares the exact same time source as the production path; it only controls what "now" is.
		/// </summary>
		/// <remarks>
		/// This is intended only for unit tests that require a fake clock. Always pair with <see cref="ResetTimeOverride"/>.
		/// </remarks>
		/// <param name="now">The time to use as <see cref="Now"/>.</param>
		internal static void OverrideTime(DateTime now)
		{
			Now = now;
			IsTimeOverridden = true;
		}

		/// <summary>
		/// Removes any time override applied by <see cref="OverrideTime"/> and resumes tracking <see cref="DateTime.UtcNow"/>.
		/// </summary>
		internal static void ResetTimeOverride()
		{
			IsTimeOverridden = false;
			UpdateTime();
		}

		[System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "IDE0052:Remove unread private members", Justification = "Establishes timer and prevents it being garbage collected")]
		private static readonly Timer DateTimeTimer = new(state => UpdateTime(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
	}
}
