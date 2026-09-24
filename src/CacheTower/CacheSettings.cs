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
		/// How long an expired entry may still be served as stale when a read uses the
		/// <see cref="CacheReadPolicy.StaleWhileRevalidate"/> read policy.
		/// </summary>
		/// <remarks>
		/// This is only consulted by opt-in stale-while-revalidate reads.
		/// When not set, such reads behave like <see cref="CacheReadPolicy.Default"/> for expired entries and require a foreground refresh.
		/// <para>
		/// The window is measured from the entry's <see cref="ICacheEntry.Expiry"/>.
		/// An entry is only eligible while <c>now &lt;= expiry + stale-while-revalidate</c>; the end of the window is treated as a miss.
		/// A failed refresh never extends this window - it is calculated solely from the stored entry's expiry.
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
		/// Configures the cache entry to have a life of <paramref name="timeToLive"/>, to be considered stale after
		/// <paramref name="staleAfter"/> and to remain servable for <paramref name="staleWhileRevalidate"/> after expiry
		/// when a read opts in to <see cref="CacheReadPolicy.StaleWhileRevalidate"/>.
		/// </summary>
		/// <param name="timeToLive">
		/// How long till a cache entry is considered expired.
		/// Expired entries are removed from the cache and will force a foreground refresh if there is a cache miss.
		/// </param>
		/// <param name="staleAfter">
		/// How long till a cache entry is considered stale or <see langword="null"/> to disable stale background refreshes.
		/// When there is a cache hit on a stale entry, a background refresh is performed.
		/// </param>
		/// <param name="staleWhileRevalidate">
		/// How long past <paramref name="timeToLive"/> an expired entry may still be returned by an opt-in stale-while-revalidate read.
		/// A refresh failure does not extend this window.
		/// </param>
		public CacheSettings(TimeSpan timeToLive, TimeSpan? staleAfter, TimeSpan staleWhileRevalidate)
		{
			TimeToLive = timeToLive;
			StaleAfter = staleAfter;
			StaleWhileRevalidate = staleWhileRevalidate;
		}
	}
}
