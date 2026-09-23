using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace CacheTower.Internal
{
	internal sealed class DateTimeScope : IDisposable
	{
		private readonly DateTimeScope? previousScope;
		private readonly bool isRoot;
		private bool disposed;

		public DateTime Now { get; private set; }

		private DateTimeScope(DateTime now, DateTimeScope? previousScope, bool isRoot)
		{
			Now = now;
			this.previousScope = previousScope;
			this.isRoot = isRoot;
		}

		public static DateTimeScope Freeze(DateTime now)
		{
			var scope = new DateTimeScope(now, currentScope, currentScope == null);
			currentScope = scope;
			return scope;
		}

		public void Advance(TimeSpan amount)
		{
			Now += amount;
		}

		public void Dispose()
		{
			if (!disposed)
			{
				if (isRoot)
				{
					currentScope = null;
				}
				else
				{
					currentScope = previousScope;
				}

				disposed = true;
			}
		}
	}

	internal static class DateTimeProvider
	{
		[ThreadStatic]
		private static DateTimeScope? currentScope;

		/// <summary>
		/// Gets the current test scope's time or <see cref="DateTime.UtcNow"/>, updated every second in production.
		/// </summary>
		public static DateTime Now => currentScope?.Now ?? DateTime.UtcNow;

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
			if (currentScope == null)
			{
				// The production timer keeps callers reading this property cheaply without storing a new mutable value.
			}
		}

		public static DateTimeScope Freeze(DateTime? now = null) =>
			DateTimeScope.Freeze(new DateTime(
				(now ?? DateTime.UtcNow).Ticks,
				DateTimeKind.Utc
			));

		public static void Advance(TimeSpan amount)
		{
			if (currentScope == null)
		{
				throw new InvalidOperationException("Time can only be advanced within a frozen test scope");
			}

			currentScope.Advance(amount);
		}

		[System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "IDE0052:Remove unread private members", Justification = "Establishes timer and prevents it being garbage collected")]
		private static readonly Timer DateTimeTimer = new(state => UpdateTime(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
	}
}
