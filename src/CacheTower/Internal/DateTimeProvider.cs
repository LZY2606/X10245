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

		/// <summary>
		/// Test-only hook (visible via <c>InternalsVisibleTo</c>) to control the clock directly.
		/// This sets the very same <see cref="Now"/> value that every production path reads,
		/// so tests exercise the real time-dependent logic rather than a parallel implementation.
		/// </summary>
		/// <param name="now">The value to set <see cref="Now"/> to.</param>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		internal static void SetNow(DateTime now) => Now = now;

		/// <summary>
		/// Test-only hook (visible via <c>InternalsVisibleTo</c>) to pause or resume the automatic
		/// timer-based updates of <see cref="Now"/>. Defaults to <see langword="true"/> which is
		/// the production behaviour; tests that use <see cref="SetNow"/> pause the timer so their
		/// fake clock is not overwritten mid-test.
		/// </summary>
		internal static bool AutoUpdateEnabled { get; set; } = true;

		[System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "IDE0052:Remove unread private members", Justification = "Establishes timer and prevents it being garbage collected")]
		private static readonly Timer DateTimeTimer = new(state =>
		{
			if (AutoUpdateEnabled)
			{
				UpdateTime();
			}
		}, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
	}
}
