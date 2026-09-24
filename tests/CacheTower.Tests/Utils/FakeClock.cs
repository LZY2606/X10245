using System;
using CacheTower.Internal;

namespace CacheTower.Tests.Utils;

/// <summary>
/// A deterministic clock for tests. It plugs into the internal DateTimeProvider clock override,
/// the single time source that every production path reads, so no parallel time source is created.
/// </summary>
public sealed class FakeClock : IDisposable
{
	private readonly object Sync = new();
	private DateTime CurrentTime;

	public FakeClock(DateTime startTime)
	{
		if (startTime.Kind != DateTimeKind.Utc)
		{
			throw new ArgumentException("The fake clock requires a UTC start time.", nameof(startTime));
		}

		CurrentTime = startTime;
		DateTimeProvider.ClockOverride = Now;
	}

	public DateTime Now()
	{
		lock (Sync)
		{
			return CurrentTime;
		}
	}

	public void Set(DateTime newTime)
	{
		lock (Sync)
		{
			CurrentTime = newTime;
		}
	}

	public void Advance(TimeSpan duration)
	{
		lock (Sync)
		{
			CurrentTime += duration;
		}
	}

	public void Dispose()
	{
		DateTimeProvider.ClockOverride = null;
	}
}
