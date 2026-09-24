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
		/// How long after expiry a cache entry can still be served while a refresh happens in the background.
		/// </summary>
		/// <remarks>
		/// This is an opt-in stale-while-revalidate read strategy. When <see langword="null"/>, expired entries are never returned and the default policy is unchanged.
		/// </remarks>
		public TimeSpan? StaleWhileRevalidateWindow { get; }

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
			StaleWhileRevalidateWindow = null;
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
			StaleWhileRevalidateWindow = null;
		}

		/// <summary>
		/// Configures the cache entry to have a life of <paramref name="timeToLive"/>, to be considered stale after <paramref name="staleAfter"/>
		/// and to opt-in to the stale-while-revalidate read strategy with the given <paramref name="staleWhileRevalidateWindow"/>.
		/// </summary>
		/// <remarks>
		/// When a cache entry is expired but still within <see cref="CacheEntry{T}.Expiry"/> + <paramref name="staleWhileRevalidateWindow"/>,
		/// the expired value may be returned while a single background refresh is triggered for that key.
		/// A failed refresh does not extend this window; it is always measured from the original expiry.
		/// </remarks>
		/// <param name="timeToLive">
		/// How long till a cache entry is considered expired.
		/// </param>
		/// <param name="staleAfter">
		/// How long till a cache entry is considered stale.
		/// </param>
		/// <param name="staleWhileRevalidateWindow">
		/// How long after expiry the expired value can still be served while a background refresh happens. Must be positive.
		/// </param>
		/// <exception cref="ArgumentOutOfRangeException"></exception>
		public CacheSettings(TimeSpan timeToLive, TimeSpan staleAfter, TimeSpan staleWhileRevalidateWindow)
		{
			if (staleWhileRevalidateWindow <= TimeSpan.Zero)
			{
				throw new ArgumentOutOfRangeException(nameof(staleWhileRevalidateWindow), "The stale-while-revalidate window must be a positive timespan.");
			}

			TimeToLive = timeToLive;
			StaleAfter = staleAfter;
			StaleWhileRevalidateWindow = staleWhileRevalidateWindow;
		}
	}
}
