using System;
using System.Threading;

namespace CacheTower;

/// <summary>
/// Options for an opt-in read through a cache stack.
/// </summary>
/// <remarks>
/// The default value keeps the cache stack's standard behaviour - no stale-while-revalidate window and no cancellation.
/// </remarks>
public readonly record struct CacheAccessOptions
{
	/// <summary>
	/// How long after expiry an expired value may still be served while a single-flight refresh happens in the background.
	/// </summary>
	/// <remarks>
	/// <para>
	/// When set, a read that finds an entry expired for no longer than this window returns the expired value and triggers
	/// at most one background refresh per key (stale-while-revalidate).
	/// </para>
	/// <para>
	/// When <see langword="null"/> (the default), expired entries are never served and a foreground refresh is performed -
	/// preserving the standard cache stack behaviour.
	/// </para>
	/// </remarks>
	public TimeSpan? StaleWhileRevalidate { get; init; }

	/// <summary>
	/// A cancellation token used to cancel the calling read's wait.
	/// </summary>
	/// <remarks>
	/// Cancelling this token only cancels the current caller's wait - a shared refresh that is already in flight
	/// continues to completion so other readers are not affected.
	/// </remarks>
	public CancellationToken CancellationToken { get; init; }

	/// <summary>
	/// Creates read options with a stale-while-revalidate window.
	/// </summary>
	/// <param name="staleWhileRevalidate">How long after expiry an expired value may still be served.</param>
	/// <param name="cancellationToken">An optional cancellation token for the calling read's wait.</param>
	/// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="staleWhileRevalidate"/> is negative.</exception>
	public CacheAccessOptions(TimeSpan staleWhileRevalidate, CancellationToken cancellationToken = default)
	{
		if (staleWhileRevalidate < TimeSpan.Zero)
		{
			throw new ArgumentOutOfRangeException(nameof(staleWhileRevalidate), staleWhileRevalidate, "The stale-while-revalidate window cannot be negative.");
		}

		StaleWhileRevalidate = staleWhileRevalidate;
		CancellationToken = cancellationToken;
	}
}

/// <summary>
/// The result of an opt-in read through a cache stack - the value plus a receipt describing how it was resolved.
/// </summary>
/// <typeparam name="T">The type of the value.</typeparam>
public readonly record struct CacheAccessResult<T>
{
	/// <summary>
	/// The value that was read. The receipt never changes this value.
	/// </summary>
	public T? Value { get; init; }

	/// <summary>
	/// The receipt describing where the value came from and how fresh it is.
	/// </summary>
	public CacheReadReceipt Receipt { get; init; }

	/// <summary>
	/// Creates a new read result.
	/// </summary>
	public CacheAccessResult(T? value, CacheReadReceipt receipt)
	{
		Value = value;
		Receipt = receipt;
	}
}
