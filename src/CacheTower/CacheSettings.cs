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
		/// How long an expired cache entry may still be served while a single refresh is performed in the background (stale-while-revalidate).
		/// </summary>
		/// <remarks>
		/// <para>
		/// This is an opt-in policy: when <see langword="null"/> (the default), expired entries are never returned and a refresh blocks the caller, matching the default cache behaviour.
		/// </para>
		/// <para>
		/// When set, an entry that has expired but is still within <c>Expiry + StaleWhileRevalidate</c> is returned immediately with a stale source receipt
		/// while exactly one background refresh is triggered for the cache key (single-flight). Once the window elapses, reads fall back to the default blocking refresh.
		/// </para>
		/// <para>
		/// A failed refresh never extends this window - the underlying expiry is unchanged, so reads after the window elapses block again.
		/// </para>
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
		/// Configures the cache entry to have a life of <paramref name="timeToLive"/>, to be considered stale after <paramref name="staleAfter"/>
		/// and, when expired, to remain servable for the additional <paramref name="staleWhileRevalidate"/> window while a single background refresh runs.
		/// </summary>
		/// <remarks>
		/// This is the opt-in stale-while-revalidate policy. With a <paramref name="staleWhileRevalidate"/> of <see cref="TimeSpan.Zero"/>,
		/// an expired entry is not servable but the policy is still considered enabled for source reporting purposes; use the two-argument constructor for fully default behaviour.
		/// </remarks>
		/// <param name="timeToLive">How long till a cache entry is considered expired.</param>
		/// <param name="staleAfter">How long till a cache entry is considered stale. Use <see cref="TimeSpan.Zero"/> to become stale immediately.</param>
		/// <param name="staleWhileRevalidate">How long after expiry an entry may be served while a single background refresh runs. Must not be negative.</param>
		/// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="staleWhileRevalidate"/> is negative.</exception>
		public CacheSettings(TimeSpan timeToLive, TimeSpan staleAfter, TimeSpan staleWhileRevalidate)
		{
			if (staleWhileRevalidate < TimeSpan.Zero)
			{
				throw new ArgumentOutOfRangeException(nameof(staleWhileRevalidate), staleWhileRevalidate, "The stale-while-revalidate window must not be negative.");
			}

			TimeToLive = timeToLive;
			StaleAfter = staleAfter;
			StaleWhileRevalidate = staleWhileRevalidate;
		}
	}
}
