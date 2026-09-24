using System;

namespace CacheTower;

/// <summary>
/// Controls how a <see cref="ICacheStack"/> read treats an expired cache entry.
/// </summary>
public enum CacheReadMode
{
	/// <summary>
	/// Use the default behaviour: expired entries are never returned and a foreground refresh is required.
	/// </summary>
	Default = 0,
	/// <summary>
	/// Return an expired entry while it remains in the stale-while-revalidate window and refresh it in the background.
	/// </summary>
	StaleWhileRevalidate = 1
}

/// <summary>
/// Optional read behaviour for <see cref="ICacheStack.GetOrSetAsync{T}(string, Func{T, Task{T}}, CacheSettings, CacheReadOptions, System.Threading.CancellationToken)"/>.
/// </summary>
public readonly record struct CacheReadOptions
{
	/// <summary>
	/// The read strategy to use.
	/// </summary>
	public CacheReadMode Mode { get; }

	/// <summary>
	/// How long an expired entry may still be returned while a refresh is started.
	/// </summary>
	public TimeSpan? StaleWhileRevalidate { get; }

	/// <summary>
	/// Creates options for the specified read <paramref name="mode"/>.
	/// </summary>
	/// <param name="mode">The read strategy to use.</param>
	/// <param name="staleWhileRevalidate">The window in which an expired value may still be returned.</param>
	/// <exception cref="ArgumentException"></exception>
	public CacheReadOptions(CacheReadMode mode, TimeSpan? staleWhileRevalidate = null)
	{
		if (!Enum.IsDefined(typeof(CacheReadMode), mode))
		{
			throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown cache read mode.");
		}

		if (staleWhileRevalidate.HasValue && staleWhileRevalidate.Value < TimeSpan.Zero)
		{
			throw new ArgumentOutOfRangeException(nameof(staleWhileRevalidate), staleWhileRevalidate, "The stale-while-revalidate window cannot be negative.");
		}

		if (mode == CacheReadMode.StaleWhileRevalidate && !staleWhileRevalidate.HasValue)
		{
			throw new ArgumentException("A stale-while-revalidate window is required.", nameof(staleWhileRevalidate));
		}

		if (mode == CacheReadMode.Default && staleWhileRevalidate.HasValue)
		{
			throw new ArgumentException("A stale-while-revalidate window cannot be specified for the default mode.", nameof(staleWhileRevalidate));
		}

		Mode = mode;
		StaleWhileRevalidate = staleWhileRevalidate;
	}

	/// <summary>
	/// Creates stale-while-revalidate options with the specified <paramref name="staleWhileRevalidate"/> window.
	/// </summary>
	/// <param name="staleWhileRevalidate">The window in which an expired value may still be returned.</param>
	public CacheReadOptions(TimeSpan staleWhileRevalidate) : this(CacheReadMode.StaleWhileRevalidate, staleWhileRevalidate)
	{
	}
}
