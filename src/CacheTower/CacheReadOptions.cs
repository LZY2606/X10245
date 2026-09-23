using System;

namespace CacheTower;

/// <summary>
/// Controls how a <see cref="CacheStack"/> reads a value when a cache entry is unavailable.
/// </summary>
public enum CacheReadStrategy
{
	/// <summary>
	/// Uses the default read behaviour. Expired entries force a foreground refresh.
	/// </summary>
	Default = 0,
	/// <summary>
	/// Returns an expired entry while a single background refresh is in progress.
	/// </summary>
	StaleWhileRevalidate = 1
}

/// <summary>
/// Options for reading a value from a <see cref="CacheStack"/>.
/// </summary>
public readonly record struct CacheReadOptions
{
	/// <summary>
	/// The strategy used when an entry is expired.
	/// </summary>
	public CacheReadStrategy Strategy { get; }

	/// <summary>
	/// How long after expiry an entry may be served by <see cref="CacheReadStrategy.StaleWhileRevalidate"/>.
	/// </summary>
	public TimeSpan? StaleWhileRevalidate { get; }

	/// <summary>
	/// Creates options that keep the existing read behaviour.
	/// </summary>
	public CacheReadOptions()
	{
		Strategy = CacheReadStrategy.Default;
		StaleWhileRevalidate = null;
	}

	/// <summary>
	/// Enables stale-while-revalidate for the specified <paramref name="staleWhileRevalidate"/> window.
	/// </summary>
	/// <param name="staleWhileRevalidate">How long after expiry an expired value may be returned.</param>
	public CacheReadOptions(TimeSpan staleWhileRevalidate)
	{
		Strategy = CacheReadStrategy.StaleWhileRevalidate;
		StaleWhileRevalidate = staleWhileRevalidate;
	}

	internal CacheReadOptions(CacheReadStrategy strategy, TimeSpan? staleWhileRevalidate)
	{
		Strategy = strategy;
		StaleWhileRevalidate = staleWhileRevalidate;
	}
}
