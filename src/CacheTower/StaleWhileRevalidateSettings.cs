using System;

namespace CacheTower
{
	/// <summary>
	/// Cache settings for the opt-in stale-while-revalidate read strategy.
	/// </summary>
	/// <remarks>
	/// While an entry is unexpired it is returned directly.
	/// Once expired, the entry may still be served for up to <see cref="StaleWindow"/> past its expiry while a single-flight
	/// background refresh runs. After that window, reads wait for a foreground single-flight refresh.
	/// </remarks>
	public readonly record struct StaleWhileRevalidateSettings
	{
		/// <summary>
		/// How long till a cache entry is considered expired.
		/// </summary>
		public TimeSpan TimeToLive { get; }

		/// <summary>
		/// How long past expiry an expired entry may still be served as stale.
		/// </summary>
		/// <remarks>
		/// A failed refresh never extends this window - it is always measured from the original entry's expiry.
		/// A value of <see cref="TimeSpan.Zero"/> disables serving stale data, making the strategy behave as a miss-once-expired read.
		/// </remarks>
		public TimeSpan StaleWindow { get; }

		/// <summary>
		/// Configures stale-while-revalidate with the given <paramref name="timeToLive"/> and <paramref name="staleWindow"/>.
		/// </summary>
		/// <param name="timeToLive">How long till a cache entry is considered expired. Must be greater than <see cref="TimeSpan.Zero"/>.</param>
		/// <param name="staleWindow">How long past expiry an expired entry may still be served as stale. Must be non-negative.</param>
		/// <exception cref="ArgumentOutOfRangeException">
		/// Thrown when <paramref name="timeToLive"/> is not positive or <paramref name="staleWindow"/> is negative.
		/// </exception>
		public StaleWhileRevalidateSettings(TimeSpan timeToLive, TimeSpan staleWindow)
		{
			if (timeToLive <= TimeSpan.Zero)
			{
				throw new ArgumentOutOfRangeException(nameof(timeToLive), timeToLive, "Time to live must be greater than zero.");
			}

			if (staleWindow < TimeSpan.Zero)
			{
				throw new ArgumentOutOfRangeException(nameof(staleWindow), staleWindow, "Stale window must be non-negative.");
			}

			TimeToLive = timeToLive;
			StaleWindow = staleWindow;
		}

		/// <summary>
		/// Adapts these settings to the shared <see cref="CacheSettings"/> used by the single refresh implementation.
		/// </summary>
		/// <returns>A <see cref="CacheSettings"/> with the same time to live.</returns>
		internal CacheSettings ToCacheSettings() => new(TimeToLive);
	}
}
