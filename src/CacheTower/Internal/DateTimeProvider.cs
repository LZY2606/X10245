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
		/// <remarks>
		/// The setter is exposed to unit tests (via <c>InternalsVisibleTo</c>) to provide a fake clock.
		/// </remarks>
		public static DateTime Now { get; internal set; } = DateTime.UtcNow;

		/// <summary>
		/// Whether the internal timer is allowed to update <see cref="Now"/>. This is always <see langword="true"/> in production;
		/// unit tests that control <see cref="Now"/> themselves set this to <see langword="false"/> so the timer does not interfere.
		/// </summary>
		internal static bool AutomaticTimeUpdatesEnabled { get; set; } = true;

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
		public static void UpdateTime() => Now = DateTime.UtcNow;

		[System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "IDE0052:Remove unread private members", Justification = "Establishes timer and prevents it being garbage collected")]
		private static readonly Timer DateTimeTimer = new(state =>
		{
			if (AutomaticTimeUpdatesEnabled)
			{
				UpdateTime();
			}
		}, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
	}
}
