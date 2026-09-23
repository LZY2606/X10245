using System;
using CacheTower.Internal;

namespace CacheTower.Tests.Utils;

/// <summary>
/// A controllable clock for tests. It drives the exact same <see cref="DateTimeProvider.Now"/>
/// read path that production code uses - no parallel time source is introduced.
/// </summary>
internal static class FakeClock
{
	public static DateTime Now => DateTimeProvider.Now;

	public static void Freeze(DateTime frozenTime) => DateTimeProvider.FreezeTime(frozenTime);

	public static void Advance(TimeSpan duration) => DateTimeProvider.FreezeTime(Now + duration);

	public static void Reset() => DateTimeProvider.UnfreezeTime();
}
