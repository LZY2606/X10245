using System;
using CacheTower.Internal;

namespace CacheTower.Tests.Utils;

/// <summary>
/// A fake clock for tests, controlling <see cref="DateTimeProvider.Now"/> directly.
/// While disposed, the real clock behaviour is restored.
/// </summary>
public sealed class FakeClock : IDisposable
{
	public FakeClock(DateTime startTime)
	{
		DateTimeProvider.AutomaticTimeUpdatesEnabled = false;
		DateTimeProvider.Now = startTime;
	}

	public DateTime Now => DateTimeProvider.Now;

	public void Set(DateTime time)
	{
		DateTimeProvider.Now = time;
	}

	public void Advance(TimeSpan duration)
	{
		DateTimeProvider.Now = DateTimeProvider.Now.Add(duration);
	}

	public void Dispose()
	{
		DateTimeProvider.Now = DateTime.UtcNow;
		DateTimeProvider.AutomaticTimeUpdatesEnabled = true;
	}
}
