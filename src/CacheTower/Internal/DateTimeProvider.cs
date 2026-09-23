using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace CacheTower.Internal
{
internal static class DateTimeProvider
{
	/// <summary>
	/// The current <see cref="DateTime.UtcNow"/>, updated every second.
	/// </summary>
	public static DateTime Now { get; private set; } = DateTime.UtcNow;

	private static DateTime? FixedNow { get; set; }

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
	public static void UpdateTime() => Now = DateTime.UtcNow;

	internal static IDisposable UseFixedTime(DateTime fixedTime)
	{
		var fixedNow = new DateTime(
			fixedTime.Year, fixedTime.Month, fixedTime.Day,
			fixedTime.Hour, fixedTime.Minute, fixedTime.Second,
			fixedTime.Millisecond, DateTimeKind.Utc
		);

		lock (typeof(DateTimeProvider))
		{
			FixedNow = fixedNow;
			Now = fixedNow;
		}

		return new FixedTimeScope();
	}

	internal static void Advance(TimeSpan timeSpan)
	{
		lock (typeof(DateTimeProvider))
		{
			if (FixedNow.HasValue)
			{
				FixedNow = FixedNow.Value + timeSpan;
				Now = FixedNow.Value;
			}
			else
			{
				UpdateTime();
			}
		}
	}

	private sealed class FixedTimeScope : IDisposable
	{
		private bool disposed;

		public void Dispose()
		{
			if (disposed)
			{
				return;
			}

			disposed = true;

			lock (typeof(DateTimeProvider))
			{
				FixedNow = null;
				UpdateTime();
			}
		}
	}

	[System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "IDE0052:Remove unread private members", Justification = "Establishes timer and prevents it being garbage collected")]
	private static readonly Timer DateTimeTimer = new(state =>
	{
		lock (typeof(DateTimeProvider))
		{
			if (!FixedNow.HasValue)
			{
				UpdateTime();
			}
		}
	}, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
}
}
