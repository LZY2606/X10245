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
		/// When <see langword="true"/>, the automatic timer no longer updates <see cref="Now"/>.
		/// </summary>
		/// <remarks>
		/// This is a test-only hook (visible via <c>InternalsVisibleTo</c>) that allows a fake clock to control time.
		/// It gates the same <see cref="Now"/> value that every production path reads, so frozen-time tests exercise the real time logic.
		/// </remarks>
		internal static bool IsTimeFrozen { get; set; }

		/// <summary>
		/// Sets <see cref="Now"/> to the given <paramref name="now"/>.
		/// </summary>
		/// <remarks>
		/// This is a test-only hook (visible via <c>InternalsVisibleTo</c>) intended to be used together with <see cref="IsTimeFrozen"/>.
		/// It writes the same <see cref="Now"/> value that every production path reads - no parallel time source is introduced.
		/// </remarks>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		internal static void SetTime(DateTime now) => Now = now;

		[System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "IDE0052:Remove unread private members", Justification = "Establishes timer and prevents it being garbage collected")]
		private static readonly Timer DateTimeTimer = new(state =>
		{
			if (!IsTimeFrozen)
			{
				UpdateTime();
			}
		}, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
	}
}
