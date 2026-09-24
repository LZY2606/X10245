using System;

namespace CacheTower
{
	/// <summary>
	/// Cache settings used by a cache stack to evaluate whether a cache entry is stale or expired.
	/// </summary>
	public readonly record struct CacheSettings
	{
		/// <summary>
		/// How long till a cache entry is considered expired.
		/// </summary>
		public TimeSpan TimeToLive { get; }
		/// <summary>
		/// How long till a cache entry is considered stale.
		/// </summary>
		/// <remarks>
		/// While optional, the cache will not perform a background refresh if <see cref="StaleAfter"/> is not set.
		/// </remarks>
	public TimeSpan? StaleAfter { get; }
	/// <summary>
	/// How long an expired entry may be served while its value is revalidated.
	/// </summary>
	/// <remarks>
	/// When not set, expired entries force a foreground refresh as in previous versions.
	/// </remarks>
	public TimeSpan? StaleWhileRevalidate { get; }

		/// <summary>
		/// Configures the cache entry to have a life of <paramref name="timeToLive"/>.
		/// </summary>
		/// <param name="timeToLive">
		/// How long till a cache entry is considered expired.
		/// Expired entries are removed from the cache and will force a foreground refresh if there is a cache miss.
		/// </param>
	public CacheSettings(TimeSpan timeToLive)
	{
		TimeToLive = timeToLive;
		StaleAfter = null;
		StaleWhileRevalidate = null;
	}

		/// <summary>
		/// Configures the cache entry to have a life of <paramref name="timeToLive"/> and to be considered stale after <paramref name="staleAfter"/>.
		/// </summary>
		/// <remarks>
		/// When there is a cache hit on a stale cache item, a background refresh will be performed.
		/// </remarks>
		/// <param name="timeToLive">
		/// How long till a cache entry is considered expired.
		/// Expired entries are removed from the cache and will force a foreground refresh if there is a cache miss.
		/// </param>
		/// <param name="staleAfter">
		/// How long till a cache entry is considered stale.
		/// When there is a cache hit on a stale entry, a background refresh is performed.
		/// <para>
		/// Setting this too low will cause potentially unnecessary background refreshes.
		/// Setting this too high may limit the usefulness of a stale time.
		/// You will need to decide on the appropriate value based on your own usage.
		/// </para>
		/// </param>
	public CacheSettings(TimeSpan timeToLive, TimeSpan staleAfter)
	{
		TimeToLive = timeToLive;
		StaleAfter = staleAfter;
		StaleWhileRevalidate = null;
	}

	/// <summary>
	/// Configures the cache entry with stale-while-revalidate behavior for expired entries.
	/// </summary>
	/// <param name="timeToLive">How long till a cache entry is considered expired.</param>
	/// <param name="staleAfter">How long till a still-unexpired entry is considered stale and eligible for background refresh.</param>
	/// <param name="staleWhileRevalidate">How long after expiry an entry may be returned while a single refresh is performed.</param>
	public CacheSettings(TimeSpan timeToLive, TimeSpan staleAfter, TimeSpan staleWhileRevalidate)
	{
		if (staleWhileRevalidate < TimeSpan.Zero)
		{
			throw new ArgumentOutOfRangeException(nameof(staleWhileRevalidate), "Stale-while-revalidate must not be negative.");
		}

		TimeToLive = timeToLive;
		StaleAfter = staleAfter;
		StaleWhileRevalidate = staleWhileRevalidate;
	}
}
}
