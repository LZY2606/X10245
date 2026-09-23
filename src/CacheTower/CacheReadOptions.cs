using System;
using System.Threading;

namespace CacheTower;

/// <summary>
/// Options for a single cache read performed through <see cref="ICacheStack.GetOrSetAsync{T}(string, Func{T, System.Threading.Tasks.Task{T}}, CacheSettings, CacheReadOptions)"/>.
/// </summary>
/// <remarks>
/// The default options preserve the historical read policy exactly: expired values are never served and no stale-while-revalidate window is applied.
/// </remarks>
public readonly record struct CacheReadOptions
{
	/// <summary>
	/// The maximum time past a cache entry's expiry during which the expired value may still be returned while a single refresh is performed in the background.
	/// </summary>
	/// <remarks>
	/// <para>
	/// Defaults to <see langword="null"/>, which keeps the default policy: an expired entry is never returned and a refresh runs on the calling thread.
	/// </para>
	/// <para>
	/// When set, a value that expired no more than <see cref="StaleWhileRevalidateWindow"/> ago is returned immediately with a stale
	/// <see cref="CacheReadReceipt"/> and exactly one background refresh is triggered for the key. Concurrent reads of the same key join
	/// that single refresh. Once the window has elapsed, reads fall back to the default, foreground-refresh behaviour.
	/// </para>
	/// <para>
	/// The window boundary is inclusive: at exactly <c>expiry + window</c> the value is still served stale.
	/// A failed refresh never extends the window - the original expiry is unchanged.
	/// </para>
	/// </remarks>
	public TimeSpan? StaleWhileRevalidateWindow { get; }

	/// <summary>
	/// A cancellation token scoped to the calling read.
	/// </summary>
	/// <remarks>
	/// Cancelling the token cancels only the calling read's wait. It never cancels a shared refresh already in progress - that refresh
	/// runs to completion so other callers and subsequent reads can use its result.
	/// </remarks>
	public CancellationToken CancellationToken { get; }

	/// <summary>
	/// Creates read options with the given stale-while-revalidate <paramref name="staleWhileRevalidateWindow"/> and optional
	/// <paramref name="cancellationToken"/>.
	/// </summary>
	/// <param name="staleWhileRevalidateWindow">
	/// The maximum time past expiry during which an expired value may still be served. Must be non-negative.
	/// Pass <see langword="null"/> to keep the default policy that never serves expired values.
	/// </param>
	/// <param name="cancellationToken">A token that cancels the calling read's wait without cancelling shared refreshes.</param>
	/// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="staleWhileRevalidateWindow"/> is negative.</exception>
	public CacheReadOptions(TimeSpan? staleWhileRevalidateWindow = null, CancellationToken cancellationToken = default)
	{
		if (staleWhileRevalidateWindow < TimeSpan.Zero)
		{
			throw new ArgumentOutOfRangeException(nameof(staleWhileRevalidateWindow), staleWhileRevalidateWindow, "The stale-while-revalidate window must not be negative.");
		}

		StaleWhileRevalidateWindow = staleWhileRevalidateWindow;
		CancellationToken = cancellationToken;
	}
}
