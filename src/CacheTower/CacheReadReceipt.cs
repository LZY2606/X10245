using System;

namespace CacheTower;

/// <summary>
/// Describes where the value returned from a read came from.
/// </summary>
public enum CacheValueSource
{
	/// <summary>
	/// The source of the value is unknown. This is only used by the default value of a receipt and is never returned from a successful read.
	/// </summary>
	Unknown = 0,
	/// <summary>
	/// The value was served from the first (typically in-memory) cache layer.
	/// </summary>
	MemoryLayer = 1,
	/// <summary>
	/// The value was served from a fallback cache layer (that is, any layer other than the first one).
	/// </summary>
	BackingLayer = 2,
	/// <summary>
	/// The value was produced by the value factory.
	/// </summary>
	Factory = 3
}

/// <summary>
/// Describes the freshness of a value at the moment it was read.
/// </summary>
public enum CacheDataFreshness
{
	/// <summary>
	/// The freshness is unknown. This is only used by the default value of a receipt and is never returned from a successful read.
	/// </summary>
	Unknown = 0,
	/// <summary>
	/// The value was fresh (not yet stale or expired).
	/// </summary>
	Fresh = 1,
	/// <summary>
	/// The value was stale (past <see cref="CacheSettings.StaleAfter"/> but not expired).
	/// </summary>
	Stale = 2,
	/// <summary>
	/// The value was expired but still served because it was within the opt-in stale-while-revalidate window.
	/// </summary>
	ExpiredButServed = 3
}

/// <summary>
/// A receipt describing how a single read through a cache stack was resolved.
/// </summary>
/// <remarks>
/// <para>
/// The receipt is diagnostic information only - it never changes the <see cref="CacheAccessResult{T}.Value"/> that is returned.
/// </para>
/// <para>
/// Combine <see cref="Source"/>, <see cref="Freshness"/> and <see cref="BackgroundRefresh"/> to distinguish the
/// different outcomes of a read, for example a fresh memory hit, a stale backing layer hit or a factory-produced value.
/// </para>
/// </remarks>
public readonly record struct CacheReadReceipt
{
	/// <summary>
	/// Where the served value came from.
	/// </summary>
	public CacheValueSource Source { get; init; }

	/// <summary>
	/// The freshness of the served value.
	/// </summary>
	public CacheDataFreshness Freshness { get; init; }

	/// <summary>
	/// The index of the cache layer the value was served from, or <c>-1</c> if the value came from the factory.
	/// </summary>
	public int LayerIndex { get; init; }

	/// <summary>
	/// Whether a single-flight background refresh was triggered by this read.
	/// </summary>
	/// <remarks>
	/// Only one concurrent read for a key will ever observe <see langword="true"/> - the other reads join the same refresh.
	/// </remarks>
	public bool BackgroundRefresh { get; init; }

	/// <summary>
	/// The expiry of the served entry, if one existed when reading.
	/// </summary>
	public DateTime? Expiry { get; init; }

	/// <summary>
	/// Creates a receipt for a value served by a cache layer.
	/// </summary>
	internal static CacheReadReceipt FromLayer(int layerIndex, CacheDataFreshness freshness, DateTime expiry, bool backgroundRefresh) => new()
	{
		Source = layerIndex == 0 ? CacheValueSource.MemoryLayer : CacheValueSource.BackingLayer,
		Freshness = freshness,
		LayerIndex = layerIndex,
		BackgroundRefresh = backgroundRefresh,
		Expiry = expiry
	};

	/// <summary>
	/// Creates a receipt for a value produced by the factory.
	/// </summary>
	internal static CacheReadReceipt FromFactory(DateTime expiry) => new()
	{
		Source = CacheValueSource.Factory,
		Freshness = CacheDataFreshness.Fresh,
		LayerIndex = -1,
		BackgroundRefresh = false,
		Expiry = expiry
	};
}
