using System;
using CacheTower.Internal;

namespace CacheTower.Tests.Utils
{
	/// <summary>
	/// A fake clock installed into <see cref="DateTimeProvider"/> for deterministic, time-based tests.
	/// All expiry, staleness and stale-while-revalidate window calculations in the cache stack read
	/// <see cref="DateTimeProvider.Now"/>, so this clock drives the exact production code paths.
	/// </summary>
	public sealed class FakeClock
	{
		/// <summary>
		/// A fixed, whole-second default start time so <see cref="CacheEntry{T}"/> expiry rounding never affects assertions.
		/// </summary>
		public static readonly DateTime DefaultStart = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

		private FakeClock(DateTime start)
		{
			Now = start;
		}

		/// <summary>
		/// The current fake time.
		/// </summary>
		public DateTime Now { get; private set; }

		/// <summary>
		/// Installs the fake clock as the time source for the cache stack. Only one clock can be installed at a time.
		/// </summary>
		public static FakeClock Install(DateTime? start = null)
		{
			var clock = new FakeClock(start ?? DefaultStart);
			DateTimeProvider.NowOverride = () => clock.Now;
			return clock;
		}

		/// <summary>
		/// Removes the fake clock, restoring the production time source.
		/// </summary>
		public static void Uninstall()
		{
			DateTimeProvider.NowOverride = null;
		}

		/// <summary>
		/// Sets the current fake time.
		/// </summary>
		public void Set(DateTime now)
		{
			Now = now;
		}

		/// <summary>
		/// Advances the current fake time by <paramref name="duration"/>.
		/// </summary>
		public void Advance(TimeSpan duration)
		{
			Now += duration;
		}
	}
}
