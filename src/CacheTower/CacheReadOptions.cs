using System;

namespace CacheTower;

/// <summary>
/// Options for a single read through an <see cref="ICacheStack"/>.
/// </summary>
/// <remarks>
/// The default value of <see cref="CacheReadOptions"/> preserves the historical read behaviour of
/// <see cref="ICacheStack.GetOrSetAsync{T}(string, Func{T, System.Threading.Tasks.Task{T}}, CacheSettings)"/> -
/// expired entries are never returned and a cache miss forces a foreground refresh.
/// </remarks>
public readonly record struct CacheReadOptions
{
	/// <summary>
	/// The default read policy. Expired entries are not served and no stale-while-revalidate window applies.
	/// </summary>
	public static readonly CacheReadOptions Default = default;

	/// <summary>
	/// How long after an entry's <see cref="ICacheEntry.Expiry"/> it may still be served as stale while a
	/// single shared refresh is performed.
	/// </summary>
	/// <remarks>
	/// When <see langword="null"/>, expired entries are never served (the default policy).
	/// An entry is eligible for stale-while-revalidate while <c>Expiry &lt;= now &lt;= Expiry + StaleWhileRevalidate</c>.
	/// </remarks>
	public TimeSpan? StaleWhileRevalidate { get; }

	/// <summary>
	/// Creates read options with the specified stale-while-revalidate <paramref name="window"/>.
	/// </summary>
	/// <param name="window">
	/// How long after expiry an entry may still be served while a shared refresh is triggered.
	/// Must be non-negative.
	/// </param>
	/// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="window"/> is negative.</exception>
	public CacheReadOptions(TimeSpan window)
	{
		if (window < TimeSpan.Zero)
		{
			throw new ArgumentOutOfRangeException(nameof(window), window, "The stale-while-revalidate window must not be negative.");
		}

		StaleWhileRevalidate = window;
	}

	private CacheReadOptions(TimeSpan? window)
	{
		StaleWhileRevalidate = window;
	}
}
