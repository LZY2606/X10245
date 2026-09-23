using System;
using CacheTower.Internal;

namespace CacheTower.Tests.StaleWhileRevalidate
{
	/// <summary>
	/// A deterministic fake clock that drives the production code through the internal
	/// <see cref="DateTimeProvider"/> test seam. It exercises the same expiry/stale code paths as
	/// production - no caching algorithm is duplicated here.
	/// </summary>
	internal readonly struct FakeClockScope : IDisposable
	{
		private readonly IDisposable scope;

		public DateTime Now => DateTimeProvider.Now;

		public FakeClockScope(DateTime startTime)
		{
			scope = DateTimeProvider.UseFrozenTime(DateTime.SpecifyKind(startTime, DateTimeKind.Utc));
		}

		public void Advance(TimeSpan delta) => DateTimeProvider.AdvanceFrozenTime(delta);

		public void Dispose() => scope.Dispose();
	}
}
