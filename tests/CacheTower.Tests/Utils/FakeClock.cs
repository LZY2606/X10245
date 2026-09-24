using System;
using CacheTower.Internal;

namespace CacheTower.Tests.Utils;

/// <summary>
/// A controllable clock that replaces <see cref="DateTimeProvider.Now"/> for the duration of a test.
/// The production code under test reads the same <see cref="DateTimeProvider.Now"/> property,
/// so time-dependent behaviour is exercised through the real code path.
/// </summary>
internal sealed class FakeClock : IDisposable
{
	private DateTime currentTime;

	public FakeClock(DateTime startTime)
	{
		currentTime = startTime;
		DateTimeProvider.NowOverride = () => currentTime;
	}

	public DateTime Now => currentTime;

	public void Set(DateTime time) => currentTime = time;

	public void Advance(TimeSpan duration) => currentTime = currentTime.Add(duration);

	public void Dispose() => DateTimeProvider.NowOverride = null;
}
