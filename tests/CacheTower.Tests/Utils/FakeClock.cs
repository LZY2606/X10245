using System;
using CacheTower.Internal;

namespace CacheTower.Tests.Utils;

/// <summary>
/// A controllable clock for tests. It overrides the exact same <see cref="DateTimeProvider.Now"/>
/// source that the production cache stack, cache entries and cache layers read - no parallel time logic.
/// </summary>
internal sealed class FakeClock : IDisposable
{
	private DateTime currentTime;

	public FakeClock(DateTime startTime)
	{
		currentTime = startTime;
		DateTimeProvider.OverrideTimeSource(() => currentTime);
	}

	public DateTime Now => currentTime;

	public void Set(DateTime time) => currentTime = time;

	public void Advance(TimeSpan duration) => currentTime += duration;

	public void Dispose() => DateTimeProvider.OverrideTimeSource(null);
}
