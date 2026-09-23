using System;
using System.Threading;

namespace CacheTower;

/// <summary>
/// Options for an opt-in stale-while-revalidate read on a cache stack.
/// </summary>
/// <remarks>
		/// Unlike the default <c>GetOrSetAsync</c> behaviour,
/// an expired cache entry may be served while a single shared refresh is performed,
/// provided it is still within <see cref="StaleWhileRevalidate"/> of its expiry.
/// </remarks>
public readonly record struct CacheReadOptions
{
	/// <summary>
	/// The maximum length of time past a cache entry's expiry during which the expired value may still be served while a refresh is in progress.
	/// </summary>
	/// <remarks>
	/// A value equal to <see cref="TimeSpan.Zero"/> only allows serving a value that expires at the exact instant it is read.
	/// Once this window elapses, reads wait for (or join the single shared) refresh like a normal cache miss.
	/// </remarks>
	public TimeSpan StaleWhileRevalidate { get; }

	/// <summary>
	/// The cancellation token used to abandon the caller's wait for a result.
	/// </summary>
	/// <remarks>
	/// Cancelling the token abandons the caller's wait but never cancels a shared refresh that other callers may depend on.
	/// The token is not passed to the value factory.
	/// </remarks>
	public CancellationToken CancellationToken { get; init; } = CancellationToken.None;

	/// <summary>
	/// Configures a stale-while-revalidate read with the given <paramref name="staleWhileRevalidate"/> window.
	/// </summary>
	/// <param name="staleWhileRevalidate">
	/// The maximum length of time past expiry during which an expired value may still be served while refreshing.
	/// </param>
	/// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="staleWhileRevalidate"/> is negative.</exception>
	public CacheReadOptions(TimeSpan staleWhileRevalidate)
	{
		if (staleWhileRevalidate < TimeSpan.Zero)
		{
			throw new ArgumentOutOfRangeException(nameof(staleWhileRevalidate), staleWhileRevalidate, "The stale-while-revalidate window cannot be negative.");
		}

		StaleWhileRevalidate = staleWhileRevalidate;
	}
}
