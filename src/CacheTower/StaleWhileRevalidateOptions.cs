using System;

namespace CacheTower;

/// <summary>
/// Options for the opt-in stale-while-revalidate read strategy.
/// </summary>
/// <remarks>
/// Expired cache entries may continue to be served for up to <see cref="StaleWindow"/> past their expiry while a single-flight refresh runs in the background.
/// A failed refresh never extends the window: it is always measured from the entry's original expiry.
/// </remarks>
public readonly record struct StaleWhileRevalidateOptions
{
	/// <summary>
	/// The cache settings used to evaluate whether a cache entry is stale or expired.
	/// </summary>
	public CacheSettings CacheSettings { get; }

	/// <summary>
	/// How long past expiry an expired cache entry may still be served while a refresh happens in the background.
	/// </summary>
	public TimeSpan StaleWindow { get; }

	/// <summary>
	/// Configures the stale-while-revalidate read strategy with the given <paramref name="cacheSettings"/> and <paramref name="staleWindow"/>.
	/// </summary>
	/// <param name="cacheSettings">The cache settings used to evaluate whether a cache entry is stale or expired.</param>
	/// <param name="staleWindow">How long past expiry an expired cache entry may still be served. Must not be negative.</param>
	/// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="staleWindow"/> is negative.</exception>
	public StaleWhileRevalidateOptions(CacheSettings cacheSettings, TimeSpan staleWindow)
	{
		if (staleWindow < TimeSpan.Zero)
		{
			throw new ArgumentOutOfRangeException(nameof(staleWindow), "The stale window cannot be negative.");
		}

		CacheSettings = cacheSettings;
		StaleWindow = staleWindow;
	}
}
