using System;

namespace CacheTower;

/// <summary>
/// Controls how a cache read handles an expired entry.
/// </summary>
public enum CacheReadPolicy
{
	/// <summary>
	/// Expired entries are never returned and a refresh is required.
	/// </summary>
	Default = 0,
	/// <summary>
	/// An expired entry may be returned while a single refresh is performed.
	/// </summary>
	StaleWhileRevalidate = 1
}

/// <summary>
/// Describes where the value returned by a cache read came from.
/// </summary>
public enum CacheValueSource
{
	/// <summary>
	/// The value came from the first cache layer.
	/// </summary>
	MemoryLayer = 0,
	/// <summary>
	/// The value came from a cache layer after the first one.
	/// </summary>
	BackingLayer = 1,
	/// <summary>
	/// The value was produced by the value factory.
	/// </summary>
	Factory = 2
}

/// <summary>
/// Describes whether the value returned by a cache read was fresh or stale.
/// </summary>
public enum CacheValueFreshness
{
	/// <summary>
	/// The value was within its configured fresh lifetime.
	/// </summary>
	Fresh = 0,
	/// <summary>
	/// The value was stale but allowed to be used.
	/// </summary>
	Stale = 1
}

/// <summary>
/// Options for performing a cache read.
/// </summary>
public readonly record struct CacheReadOptions
{
	/// <summary>
	/// The policy used when an expired cache entry is found.
	/// </summary>
	public CacheReadPolicy Policy { get; init; }

	/// <summary>
	/// The amount of time after expiry during which an expired cache entry may be returned while refreshing.
	/// </summary>
	public TimeSpan StaleWhileRevalidateWindow { get; init; }

	/// <summary>
	/// Creates default cache read options.
	/// </summary>
	public CacheReadOptions()
	{
		Policy = CacheReadPolicy.Default;
		StaleWhileRevalidateWindow = TimeSpan.Zero;
	}

	/// <summary>
	/// Creates cache read options with the specified <paramref name="policy"/> and <paramref name="staleWhileRevalidateWindow"/>.
	/// </summary>
	/// <param name="policy">The policy used when an expired cache entry is found.</param>
	/// <param name="staleWhileRevalidateWindow">The amount of time after expiry during which an expired value may be served.</param>
	/// <exception cref="ArgumentOutOfRangeException">
	/// Thrown when <paramref name="policy"/> is unknown or <paramref name="staleWhileRevalidateWindow"/> is invalid.
	/// </exception>
	public CacheReadOptions(CacheReadPolicy policy, TimeSpan staleWhileRevalidateWindow) : this()
	{
		if (staleWhileRevalidateWindow < TimeSpan.Zero)
		{
			throw new ArgumentOutOfRangeException(nameof(staleWhileRevalidateWindow), staleWhileRevalidateWindow, "The stale-while-revalidate window cannot be negative.");
		}

		if (policy == CacheReadPolicy.StaleWhileRevalidate && staleWhileRevalidateWindow == TimeSpan.Zero)
		{
			throw new ArgumentOutOfRangeException(nameof(staleWhileRevalidateWindow), staleWhileRevalidateWindow, "The stale-while-revalidate window must be greater than zero.");
		}

		if (policy != CacheReadPolicy.Default && policy != CacheReadPolicy.StaleWhileRevalidate)
		{
			throw new ArgumentOutOfRangeException(nameof(policy), policy, "Unknown cache read policy.");
		}

		Policy = policy;
			StaleWhileRevalidateWindow = staleWhileRevalidateWindow;
	}

	/// <summary>
	/// Creates options that allow an expired value to be returned for the given window while a refresh is started.
	/// </summary>
	/// <param name="staleWhileRevalidateWindow">The amount of time after expiry during which an expired value may be served.</param>
	/// <returns></returns>
	public static CacheReadOptions AllowStaleWhileRevalidate(TimeSpan staleWhileRevalidateWindow) =>
		new(CacheReadPolicy.StaleWhileRevalidate, staleWhileRevalidateWindow);
}

/// <summary>
/// Describes the source and freshness of a cache read result.
/// </summary>
public readonly record struct CacheReadReceipt(
	CacheValueSource Source,
	CacheValueFreshness Freshness,
	int LayerIndex)
{
	internal static CacheReadReceipt FromLayer(int layerIndex, CacheValueFreshness freshness) =>
		new(layerIndex == 0 ? CacheValueSource.MemoryLayer : CacheValueSource.BackingLayer, freshness, layerIndex);

	/// <summary>
	/// Gets a receipt for a value produced by the value factory.
	/// </summary>
	public static CacheReadReceipt Factory { get; } = new(CacheValueSource.Factory, CacheValueFreshness.Fresh, -1);
}

/// <summary>
/// The value returned by a cache read and a receipt describing where it came from.
/// </summary>
/// <typeparam name="T">The type of the cached value.</typeparam>
/// <param name="Value">The cached value.</param>
/// <param name="Receipt">The receipt describing the value's source.</param>
public readonly record struct CacheReadResult<T>(T? Value, CacheReadReceipt Receipt);
