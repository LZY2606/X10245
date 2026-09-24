using System;
using CacheTower.Internal;

namespace CacheTower.Tests.Utils;

/// <summary>
/// A controllable clock that overrides <see cref="DateTimeProvider.Now"/> for the duration of a test.
/// Every time read in the production path flows through <see cref="DateTimeProvider.Now"/>,
/// so advancing this clock exercises the exact same code that real time does.
/// </summary>
public sealed class FakeClock : IDisposable
{
	private DateTime currentTime;

	public FakeClock(DateTime startTime)
	{
		if (startTime.Kind != DateTimeKind.Utc)
		{
			throw new ArgumentException("The fake clock must start at a UTC time", nameof(startTime));
		}

		currentTime = startTime;
		DateTimeProvider.SetClockOverride(() => currentTime);
	}

	public DateTime Now => currentTime;

	public void Advance(TimeSpan duration) => currentTime += duration;

	public void Dispose() => DateTimeProvider.SetClockOverride(null);
}
