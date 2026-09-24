using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace CacheTower.Internal
{
	internal static class DateTimeProvider
	{
		private static DateTime systemNow = DateTime.UtcNow;

		/// <summary>
		/// The current <see cref="DateTime.UtcNow"/>, updated every second.
		/// </summary>
		/// <remarks>
		/// Every production time read flows through this property. When <see cref="ClockOverride"/> is set
		/// (only visible to friend assemblies such as the test suite), the override supplies the exact same
		/// value that the production paths consume - there is no parallel time source.
		/// </remarks>
		public static DateTime Now => ClockOverride?.Invoke() ?? systemNow;

		/// <summary>
		/// An optional clock override used by tests (via <c>InternalsVisibleTo</c>) to control time.
		/// When <see langword="null"/>, the system clock updated by the internal timer is used.
		/// </summary>
		internal static Func<DateTime>? ClockOverride { get; set; }

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
		public static void UpdateTime() => systemNow = DateTime.UtcNow;

		[System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "IDE0052:Remove unread private members", Justification = "Establishes timer and prevents it being garbage collected")]
		private static readonly Timer DateTimeTimer = new(state => UpdateTime(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
	}
}
