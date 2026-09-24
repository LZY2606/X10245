using System;
using CacheTower.Internal;

namespace CacheTower.Tests.Utils;

/// <summary>
/// A fake clock that freezes <see cref="DateTimeProvider.Now"/> at a controlled time.
/// This uses the same <see cref="DateTimeProvider.Now"/> value that every production path reads,
/// so frozen-time tests exercise the real time logic of the cache stack.
/// </summary>
public sealed class FrozenClock : IDisposable
{
	public FrozenClock(DateTime startTime)
	{
		DateTimeProvider.IsTimeFrozen = true;
		DateTimeProvider.SetTime(startTime);
	}

	public DateTime Now => DateTimeProvider.Now;

	public void SetTime(DateTime time)
	{
		DateTimeProvider.SetTime(time);
	}

	public void Advance(TimeSpan duration)
	{
		DateTimeProvider.SetTime(DateTimeProvider.Now + duration);
	}

	public void Dispose()
	{
		DateTimeProvider.IsTimeFrozen = false;
		DateTimeProvider.UpdateTime();
	}
}
