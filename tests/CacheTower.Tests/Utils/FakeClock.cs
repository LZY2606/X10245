using System;
using CacheTower.Internal;

namespace CacheTower.Tests.Utils;

/// <summary>
/// A fake clock for tests that drives the exact same <see cref="DateTimeProvider.Now"/> value the production cache paths read.
/// Disposing the clock restores the real time source.
/// </summary>
public sealed class FakeClock : IDisposable
{
	public FakeClock(DateTime now)
	{
		Now = now;
		DateTimeProvider.OverrideNow(now);
	}

	public DateTime Now { get; private set; }

	public void Set(DateTime now)
	{
		Now = now;
		DateTimeProvider.OverrideNow(now);
	}

	public void Advance(TimeSpan duration)
	{
		Set(Now + duration);
	}

	public void Dispose()
	{
		DateTimeProvider.ResetNow();
	}
}
