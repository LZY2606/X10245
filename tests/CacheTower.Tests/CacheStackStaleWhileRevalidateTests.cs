using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CacheTower.Serializers;
using CacheTower.Tests.Utils;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CacheTower.Tests
{
	/// <summary>
	/// Contract tests for the opt-in stale-while-revalidate read policy, single-flight refreshes
	/// and per-read source receipts. All scenarios enter through the public cache stack entry points
	/// using a fake clock (<see cref="Internal.DateTimeProvider"/>) and controllable cache layers
	/// (<see cref="ControlledCacheLayer"/>). The default policy (no stale-while-revalidate window)
	/// must remain fully compatible with the previous behavior.
	/// </summary>
	[TestClass]
	public class CacheStackStaleWhileRevalidateTests : TestBase
	{
		private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

		private static CacheSettings StaleWhileRevalidateSettings => new(TimeSpan.FromSeconds(30))
		{
			StaleWhileRevalidateWindow = TimeSpan.FromSeconds(30)
		};

		private static IDisposable Use FakeClockPlaceholder => null;
	}
}
