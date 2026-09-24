using System;
using CacheTower.Internal;

namespace CacheTower.Tests.Utils
{
	/// <summary>
	/// A controllable clock that overrides <see cref="DateTimeProvider.Now"/> for the duration of a test.
	/// Dispose to restore the default clock.
	/// </summary>
	public sealed class FakeClock : IDisposable
	{
		private DateTime CurrentTime;

		public FakeClock(DateTime startTime)
		{
			CurrentTime = startTime;
			DateTimeProvider.OverrideClock(() => CurrentTime);
		}

		public static FakeClock Start() => new(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

		public DateTime Now => CurrentTime;

		public void Advance(TimeSpan timeSpan) => CurrentTime += timeSpan;

		public void Dispose() => DateTimeProvider.OverrideClock(null);
	}
}
