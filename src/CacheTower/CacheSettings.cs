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
		/// How long an expired entry may still be served while a single background refresh is performed.
		/// </summary>
		/// <remarks>
		/// This is an opt-in stale-while-revalidate policy and defaults to <see langword="null"/> (disabled) so that
		/// the default behaviour of never returning an expired entry is preserved.
		/// <para>
		/// When set, an entry that has passed its <see cref="TimeToLive"/> but not this additional window is returned to the caller
		/// while exactly one refresh is triggered for the cache key. A failed refresh never extends this window -
		/// once <see cref="TimeToLive"/> plus <see cref="StaleWhileRevalidate"/> elapses, the next read performs a foreground refresh.
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
		/// Configures the cache entry with an opt-in stale-while-revalidate policy.
		/// </summary>
		/// <param name="timeToLive">How long till a cache entry is considered expired.</param>
		/// <param name="staleAfter">How long till an unexpired entry is considered stale and eligible for a background refresh.</param>
		/// <param name="staleWhileRevalidate">
		/// How long an expired entry may still be served while a single background refresh is performed.
		/// Pass <see langword="null"/> to keep the default behaviour of forcing a foreground refresh on expiry.
		/// </param>
		/// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="staleWhileRevalidate"/> is negative.</exception>
		public CacheSettings(TimeSpan timeToLive, TimeSpan staleAfter, TimeSpan? staleWhileRevalidate)
		{
			if (staleWhileRevalidate.HasValue && staleWhileRevalidate.Value < TimeSpan.Zero)
			{
				throw new ArgumentOutOfRangeException(nameof(staleWhileRevalidate), "The stale-while-revalidate window cannot be negative.");
			}

			TimeToLive = timeToLive;
			StaleAfter = staleAfter;
			StaleWhileRevalidate = staleWhileRevalidate;
		}

		/// <summary>
		/// Calculates the latest point in time at which an entry may be served stale while a refresh is in progress.
		/// </summary>
		/// <param name="expiry">The expiry of the cache entry.</param>
		/// <returns>
		/// The stale-while-revalidate deadline, or the entry's <paramref name="expiry"/> when the policy is not enabled
		/// (meaning an expired entry is never served).
		/// </returns>
		public DateTime GetStaleWhileRevalidateDate(DateTime expiry)
		{
			return StaleWhileRevalidate.HasValue ? expiry + StaleWhileRevalidate.Value : expiry;
		}
	}
}
