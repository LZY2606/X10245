using System;
using CacheTower.Internal;

namespace CacheTower.Tests.Utils
{
	/// <summary>
	/// A fake clock for tests that overrides <see cref="DateTimeProvider.Now"/> - the exact same
	/// clock read by the production cache path. Dispose to return to the real timer-driven clock.
	/// </summary>
	public sealed class FakeClock : IDisposable
	{
		public FakeClock(DateTime now)
		{
			Now = now;
			DateTimeProvider.OverrideNow(now);
		}

		public DateTime Now { get; private set; }

		public void Advance(TimeSpan duration)
		{
			Now += duration;
			DateTimeProvider.OverrideNow(Now);
		}

		public void Set(DateTime now)
		{
			Now = now;
			DateTimeProvider.OverrideNow(now);
		}

		public void Dispose()
		{
			DateTimeProvider.OverrideNow(null);
		}
	}
}
