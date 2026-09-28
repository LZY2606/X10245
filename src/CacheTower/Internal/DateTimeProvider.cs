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

		private static bool IsFrozen;

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
			if (!IsFrozen)
			{
				Now = DateTime.UtcNow;
			}
		}

		/// <summary>
		/// Freezes <see cref="Now"/> at the supplied <paramref name="time"/>, turning the provider into a fake clock.
		/// </summary>
		/// <remarks>
		/// Test-only seam: the frozen value is the exact same <see cref="Now"/> property read by the production path, so no parallel
		/// time logic exists. Pair every call with <see cref="UnfreezeTime"/> to resume the real clock.
		/// </remarks>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		internal static void FreezeTime(DateTime time)
		{
			Now = DateTime.SpecifyKind(time, DateTimeKind.Utc);
			IsFrozen = true;
		}

		/// <summary>
		/// Moves the frozen clock forward by <paramref name="elapsed"/>.
		/// </summary>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		internal static void AdvanceTime(TimeSpan elapsed)
		{
			Now = Now.Add(elapsed);
		}

		/// <summary>
		/// Resumes the real clock after a previous <see cref="FreezeTime"/> call.
		/// </summary>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		internal static void UnfreezeTime()
		{
			IsFrozen = false;
			UpdateTime();
		}

		[System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "IDE0052:Remove unread private members", Justification = "Establishes timer and prevents it being garbage collected")]
		private static readonly Timer DateTimeTimer = new(state => UpdateTime(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
	}
}
