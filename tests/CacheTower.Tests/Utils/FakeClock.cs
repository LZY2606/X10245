using System;
using CacheTower.Internal;

namespace CacheTower.Tests.Utils
{
	/// <summary>
	/// A controllable clock for tests. While disposed correctly, it overrides <see cref="DateTimeProvider.Now"/>,
	/// which is the single time source used by all production cache code - so tests exercise the real production time path.
	/// Use whole-second timestamps: <see cref="CacheEntry{T}"/> rounds expiries down to the second.
	/// </summary>
	public sealed class FakeClock : IDisposable
	{
		public static readonly DateTime DefaultStart = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

		private DateTime currentTime;

		public FakeClock() : this(DefaultStart) { }

		public FakeClock(DateTime startTime)
		{
			currentTime = startTime;
			DateTimeProvider.OverrideTime(() => currentTime);
		}

		public DateTime Now => currentTime;

		public void Set(DateTime time)
		{
			currentTime = time;
		}

		public void Advance(TimeSpan duration)
		{
			currentTime += duration;
		}

		public void Dispose()
		{
			DateTimeProvider.OverrideTime(null);
		}
	}
}
