using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace CacheTower.Internal
{
	internal static class DateTimeProvider
	{
		private static DateTime CurrentTime = DateTime.UtcNow;

		/// <summary>
		/// A test-only clock override, visible to the test assembly via <c>InternalsVisibleTo</c>.
		/// </summary>
		/// <remarks>
		/// This is not a parallel clock: production code keeps reading <see cref="Now"/>, so tests that
		/// set the override exercise the exact same time path as production. When <see langword="null"/>,
		/// production behaviour is completely unchanged.
		/// </remarks>
		private static DateTime? TestTimeOverride;

		/// <summary>
		/// The current <see cref="DateTime.UtcNow"/>, updated every second.
		/// </summary>
		public static DateTime Now => TestTimeOverride ?? CurrentTime;

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
		public static void UpdateTime() => CurrentTime = DateTime.UtcNow;

		/// <summary>
		/// Sets (or clears, with <see langword="null"/>) a test-only override for <see cref="Now"/>.
		/// </summary>
		/// <param name="testTime">The fake time to report, or <see langword="null"/> to return to the production clock.</param>
		internal static void SetTestTimeOverride(DateTime? testTime) => TestTimeOverride = testTime;

		[System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "IDE0052:Remove unread private members", Justification = "Establishes timer and prevents it being garbage collected")]
		private static readonly Timer DateTimeTimer = new(state => UpdateTime(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
	}
}
