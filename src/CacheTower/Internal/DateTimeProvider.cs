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
		/// Pins <see cref="Now"/> to an explicit UTC <paramref name="dateTime"/>, acting as a fake clock.
		/// </summary>
		/// <remarks>
		/// This is intended only for tests that need deterministic control over stale/expiry windows and is not part of the production API.
		/// The internal timer may overwrite it with the real wall clock afterwards; call <see cref="UpdateTime"/> or this method again to re-pin it.
		/// A value with <see cref="DateTime.Kind"/> other than <see cref="DateTimeKind.Utc"/> is converted from local time.
		/// </remarks>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		internal static void SetTime(DateTime dateTime) => Now = dateTime.Kind == DateTimeKind.Utc ? dateTime : dateTime.ToUniversalTime();

		[System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "IDE0052:Remove unread private members", Justification = "Establishes timer and prevents it being garbage collected")]
		private static readonly Timer DateTimeTimer = new(state => UpdateTime(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
	}
}
