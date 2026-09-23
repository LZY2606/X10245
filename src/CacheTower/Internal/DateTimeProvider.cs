using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace CacheTower.Internal
{
	internal static class DateTimeProvider
	{
		private static readonly AsyncLocal<DateTime?> FrozenTime = new();

		/// <summary>
		/// The current time used by the cache stack.
		/// </summary>
		/// <remarks>
		/// Normally this returns the wall clock time updated once per second by an internal timer.
		/// Tests may freeze time for an async flow through <see cref="UseFrozenTime"/> so that
		/// the production refresh and expiry logic is exercised against a deterministic fake clock.
		/// </remarks>
		public static DateTime Now => FrozenTime.Value ?? DateTime.UtcNow;

		private static bool IsFrozen => FrozenTime.Value.HasValue;

		/// <summary>
		/// Updates <see cref="Now"/> to the current <see cref="DateTime.UtcNow"/> value. This is automatically called by a timer every second.
		/// </summary>
		/// <remarks>
		/// This is intended to only be triggered by the internal timer or by unit tests that require it.
		/// The reason why tests need it is due to the fast turn around of setting a value and testing the outcome.
		/// Real applications aren't immediately setting a cache value manually, calling <see cref="ICacheStack.GetOrSetAsync{T}(string, Func{T, System.Threading.Tasks.Task{T}}, CacheSettings)"/> and then comparing whether the results are the same.
		/// The alternative for the tests is just "waiting" an extra second between setting a value and retrieving it however that makes the testing slower and the tests more confusing.
		/// </remarks>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static void UpdateTime()
		{
			//When a test has frozen the clock, the timer must not override it.
			if (IsFrozen)
			{
				return;
			}
		}

		/// <summary>
		/// Freezes the provider at <paramref name="frozenTime"/> for the duration of the returned scope.
		/// </summary>
		/// <remarks>
		/// This is a test-only seam (the type is internal and only visible to the test and benchmark assemblies).
		/// It does not duplicate any production logic - expiry, stale and stale-while-revalidate checks all read
		/// <see cref="Now"/> so freezing it exercises the exact production paths.
		/// </remarks>
		public static IDisposable UseFrozenTime(DateTime frozenTime)
		{
			var previous = FrozenTime.Value;
			FrozenTime.Value = frozenTime;
			return new TimeScope(previous);
		}

		/// <summary>
		/// Advances a clock previously frozen through <see cref="UseFrozenTime"/> by <paramref name="delta"/>.
		/// </summary>
		public static void AdvanceFrozenTime(TimeSpan delta)
		{
			if (!IsFrozen)
			{
				throw new InvalidOperationException("Time can only be advanced while frozen for a test.");
			}

			FrozenTime.Value = FrozenTime.Value!.Value + delta;
		}

		private sealed class TimeScope : IDisposable
		{
			private readonly DateTime? PreviousTime;
			private bool Disposed;

			public TimeScope(DateTime? previousTime)
			{
				PreviousTime = previousTime;
			}

			public void Dispose()
			{
				if (!Disposed)
				{
					FrozenTime.Value = PreviousTime;
					Disposed = true;
				}
			}
		}

		[System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "IDE0052:Remove unread private members", Justification = "Establishes timer and prevents it being garbage collected")]
		private static readonly Timer DateTimeTimer = new(state => UpdateTime(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
	}
}
