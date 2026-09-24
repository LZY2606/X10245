using System;

namespace CacheTower;

/// <summary>
/// Options for controlling a cache stack read.
/// </summary>
public readonly record struct CacheReadOptions
{
	/// <summary>
	/// The policy used when an expired cache entry is found.
	/// </summary>
	public CacheReadPolicy Policy { get; }

	/// <summary>
	/// When <see cref="Policy"/> is <see cref="CacheReadPolicy.StaleWhileRevalidate"/>, the maximum duration past expiry that an entry may be returned.
	/// </summary>
	public TimeSpan? StaleWhileRevalidateWindow { get; }

	/// <summary>
	/// Creates read options using the default, backwards-compatible read policy.
	/// </summary>
	public CacheReadOptions()
	{
		Policy = CacheReadPolicy.Default;
		StaleWhileRevalidateWindow = null;
	}

	/// <summary>
	/// Creates read options with the supplied <paramref name="policy"/> and, optionally, a stale-while-revalidate window.
	/// </summary>
	/// <param name="policy">The policy used when an expired cache entry is found.</param>
	/// <param name="staleWhileRevalidateWindow">The maximum duration past expiry that an expired entry may be returned.</param>
	/// <exception cref="ArgumentOutOfRangeException">
	/// Thrown when <paramref name="policy"/> is not defined or when stale-while-revalidate is enabled without a positive window.
	/// </exception>
	public CacheReadOptions(CacheReadPolicy policy, TimeSpan? staleWhileRevalidateWindow = null)
	{
		if (!Enum.IsDefined(typeof(CacheReadPolicy), policy))
		{
			throw new ArgumentOutOfRangeException(nameof(policy), policy, "Unsupported cache read policy.");
		}

		if (policy == CacheReadPolicy.StaleWhileRevalidate)
		{
			if (!staleWhileRevalidateWindow.HasValue || staleWhileRevalidateWindow.Value <= TimeSpan.Zero)
			{
				throw new ArgumentOutOfRangeException(nameof(staleWhileRevalidateWindow), staleWhileRevalidateWindow, "Stale-while-revalidate requires a positive time window.");
			}
		}
		else if (staleWhileRevalidateWindow.HasValue)
		{
			throw new ArgumentException("A stale-while-revalidate window is only valid with the stale-while-revalidate policy.", nameof(staleWhileRevalidateWindow));
		}

		Policy = policy;
		StaleWhileRevalidateWindow = staleWhileRevalidateWindow;
	}
}
