using System;

namespace CacheTower;

/// <summary>
/// Options for the opt-in stale-while-revalidate read strategy on <see cref="ICacheStack"/>.
/// </summary>
/// <remarks>
/// When enabled, a cache entry that is expired but still within <see cref="StaleWindow"/> past its expiry is served to the caller
/// while a single background refresh is triggered for that cache key.
/// </remarks>
public readonly record struct StaleWhileRevalidateOptions
{
	/// <summary>
	/// How long past its expiry a cache entry may still be served while a refresh happens in the background.
	/// </summary>
	public TimeSpan StaleWindow { get; }

	/// <summary>
	/// Configures the stale-while-revalidate read strategy with the given <paramref name="staleWindow"/>.
	/// </summary>
	/// <param name="staleWindow">
	/// How long past its expiry a cache entry may still be served while a refresh happens in the background.
	/// A <see cref="TimeSpan.Zero"/> window disables serving expired entries, matching the default read strategy.
	/// </param>
	/// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="staleWindow"/> is negative.</exception>
	public StaleWhileRevalidateOptions(TimeSpan staleWindow)
	{
		if (staleWindow < TimeSpan.Zero)
		{
			throw new ArgumentOutOfRangeException(nameof(staleWindow), "The stale window cannot be negative.");
		}

		StaleWindow = staleWindow;
	}
}
