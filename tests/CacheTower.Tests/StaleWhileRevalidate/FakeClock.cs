using System;
using CacheTower.Internal;

namespace CacheTower.Tests.StaleWhileRevalidate;

/// <summary>
/// A controllable fake clock for the internal time seam the production cache stack uses.
/// It drives the exact same <see cref="DateTimeProvider"/> the production path reads - no test-only algorithm is involved.
/// </summary>
internal readonly struct FakeClock
{
	private readonly DateTime origin;

	private FakeClock(DateTime origin)
	{
		this.origin = origin;
	}

	/// <summary>
	/// Pins the shared clock to a deterministic UTC instant. Dispose (or <see cref="Reset"/>) to restore real time.
	/// </summary>
	public static FakeClock Start(DateTime? at = null)
	{
		var clock = new FakeClock(at ?? new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
		DateTimeProvider.SetTime(clock.origin);
		return clock;
	}

	public DateTime Now => DateTimeProvider.Now;

	public void Advance(TimeSpan amount)
	{
		DateTimeProvider.SetTime(DateTimeProvider.Now.Add(amount));
	}

	public void SetTo(DateTime time)
	{
		DateTimeProvider.SetTime(time);
	}

	public void Reset()
	{
		DateTimeProvider.UpdateTime();
	}
}
