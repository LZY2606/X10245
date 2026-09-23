using System;

namespace CacheTower;

/// <summary>
/// Opt-in settings for the stale-while-revalidate read strategy.
/// </summary>
/// <remarks>
/// When provided to <see cref="ICacheStack.GetOrSetWithReceiptAsync{T}(string, System.Func{T, System.Threading.Tasks.Task{T}}, CacheSettings, StaleWhileRevalidateSettings?, System.Threading.CancellationToken)"/>,
/// an expired cache entry may be returned for a limited window past its expiry while a single background refresh is triggered for the key.
/// </remarks>
public readonly record struct StaleWhileRevalidateSettings
{
	/// <summary>
	/// How long past the cache entry's expiry it may still be served as stale.
	/// </summary>
	public TimeSpan StaleWindow { get; }

	/// <summary>
	/// Configures the stale-while-revalidate read strategy with the given <paramref name="staleWindow"/>.
	/// </summary>
	/// <param name="staleWindow">
	/// How long past the cache entry's expiry it may still be served as stale.
	/// An entry is servable as stale while the current time is before <c>expiry + staleWindow</c>.
	/// </param>
	/// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="staleWindow"/> is not positive.</exception>
	public StaleWhileRevalidateSettings(TimeSpan staleWindow)
	{
		if (staleWindow <= TimeSpan.Zero)
		{
			throw new ArgumentOutOfRangeException(nameof(staleWindow), staleWindow, "The stale window must be a positive duration.");
		}

		StaleWindow = staleWindow;
	}
}
