using System;

namespace CacheTower
{
	/// <summary>
	/// Options for a single <see cref="ICacheStack.GetOrSetAsync{T}(string, Func{T, Task{T}}, CacheSettings, CacheReadOptions, System.Threading.CancellationToken)"/> read.
	/// </summary>
	/// <remarks>
	/// <para>
	/// Using the default value (<see cref="Default"/>) preserves the historical read policy exactly:
	/// expired entries are never returned and every read that needs a value blocks on the refresh.
	/// </para>
	/// <para>
	/// Specifying <see cref="StaleWhileRevalidateWindow"/> opts in to stale-while-revalidate reads:
	/// an entry that has expired may still be served for up to the window while a single shared refresh is performed in the background.
	/// </para>
	/// </remarks>
	public readonly record struct CacheReadOptions
	{
		/// <summary>
		/// The default read options with no stale-while-revalidate window.
		/// </summary>
		public static readonly CacheReadOptions Default = default;

		/// <summary>
		/// How long after its expiry an entry remains servable as stale while a background refresh is triggered.
		/// </summary>
		/// <remarks>
		/// When <see langword="null"/>, stale-while-revalidate is disabled and the default read policy is used.
		/// Once an entry is older than its expiry plus this window, it is treated as a cache miss and the value factory is called on the calling read.
		/// </remarks>
		public TimeSpan? StaleWhileRevalidateWindow { get; }

		/// <summary>
		/// Creates read options with the specified stale-while-revalidate <paramref name="window"/>.
		/// </summary>
		/// <param name="window">
		/// How long after its expiry an entry may be served stale while a single background refresh is in flight.
		/// The value must be non-negative.
		/// </param>
		/// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="window"/> is negative.</exception>
		public CacheReadOptions(TimeSpan window)
		{
			if (window < TimeSpan.Zero)
			{
				throw new ArgumentOutOfRangeException(nameof(window), window, "The stale-while-revalidate window must be non-negative.");
			}

			StaleWhileRevalidateWindow = window;
		}
	}
}
