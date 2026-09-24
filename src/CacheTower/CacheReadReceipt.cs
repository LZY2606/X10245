using System;

namespace CacheTower;

/// <summary>
/// Describes where a cached value was served from when reading through a <see cref="CacheStack"/>.
/// </summary>
public enum CacheValueSource
{
	/// <summary>
	/// The value was served from a local (in-memory) cache layer.
	/// </summary>
	MemoryLayer,
	/// <summary>
	/// The value was served from a backing (non-local) cache layer.
	/// </summary>
	BackingLayer,
	/// <summary>
	/// The value was produced by the value factory, either by this call or by a concurrent caller sharing the same single-flight refresh.
	/// </summary>
	Factory
}

/// <summary>
/// Describes whether a served value was within its expiry or served past expiry from the stale-while-revalidate window.
/// </summary>
public enum CacheValueFreshness
{
	/// <summary>
	/// The value was within its expiry when served.
	/// </summary>
	Fresh,
	/// <summary>
	/// The value was past its expiry but still inside the stale-while-revalidate window when served.
	/// </summary>
	Stale
}

/// <summary>
/// A receipt describing the origin of a value returned by a cache read.
/// The receipt is purely diagnostic - it never changes the value that is returned.
/// </summary>
/// <param name="Source">Where the value was served from.</param>
/// <param name="Freshness">Whether the value was fresh or stale when served.</param>
/// <param name="LayerIndex">
/// The index of the cache layer that served the value, ordered from the highest priority layer (0) to the lowest.
/// This is <c>-1</c> when the value was produced by the value factory.
/// </param>
public readonly record struct CacheReadReceipt(CacheValueSource Source, CacheValueFreshness Freshness, int LayerIndex)
{
	/// <summary>
	/// Whether the value was served past its expiry from the stale-while-revalidate window.
	/// </summary>
	public bool IsStale => Freshness == CacheValueFreshness.Stale;
}

/// <summary>
/// The result of a cache read that reports a <see cref="CacheReadReceipt"/> alongside the value.
/// </summary>
/// <typeparam name="T">The type of value in the cache entry.</typeparam>
/// <param name="Value">The cached or freshly produced value. This is identical to the value the equivalent non-receipt read would return.</param>
/// <param name="Receipt">The receipt describing where <paramref name="Value"/> was served from.</param>
public readonly record struct CacheReadResult<T>(T? Value, CacheReadReceipt Receipt);
