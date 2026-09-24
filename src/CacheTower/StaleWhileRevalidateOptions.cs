using System;

namespace CacheTower;

/// <summary>
/// Options for the opt-in stale-while-revalidate read policy on the cache stack.
/// </summary>
/// <remarks>
/// When an entry is expired but still within <see cref="StaleWindow"/> (measured from the entry's own expiry),
/// the expired value is returned and a single background refresh is triggered for the cache key.
/// A failed refresh never extends the window: the window is always anchored to the stored entry's expiry.
/// </remarks>
public readonly record struct StaleWhileRevalidateOptions
{
	/// <summary>
	/// How long after expiry an expired entry may still be served while it is revalidated in the background.
	/// </summary>
	public TimeSpan StaleWindow { get; }

	/// <summary>
	/// Configures the stale-while-revalidate read policy with the given <paramref name="staleWindow"/>.
	/// </summary>
	/// <param name="staleWindow">
	/// How long after expiry an expired entry may still be served.
	/// A zero window means expired entries are never served, matching the default read policy.
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
