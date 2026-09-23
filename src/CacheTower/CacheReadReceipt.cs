using System;

namespace CacheTower;

/// <summary>
/// Describes where the value returned by a cache read originated.
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
/// Describes whether a returned cache value was fresh or stale.
/// </summary>
public enum CacheValueFreshness
{
	/// <summary>
	/// The value is fresh.
	/// </summary>
	Fresh = 0,
	/// <summary>
	/// The value is stale but permitted by the selected read strategy.
	/// </summary>
	Stale = 1
}

/// <summary>
/// A value-independent description of where a cache read obtained its value.
/// </summary>
/// <param name="Source">The source that provided the value.</param>
/// <param name="Freshness">Whether the returned value was fresh or stale.</param>
/// <param name="LayerIndex">The zero-based cache layer index, or <c>-1</c> when <paramref name="Source"/> is <see cref="CacheValueSource.Factory"/>.</param>
/// <param name="LayerType">The type of cache layer that provided the value, if any.</param>
public sealed record CacheReadReceipt(
	CacheValueSource Source,
	CacheValueFreshness Freshness,
	int LayerIndex,
	Type? LayerType
)
{
	internal static CacheReadReceipt Factory { get; } = new(
		CacheValueSource.Factory,
		CacheValueFreshness.Fresh,
		-1,
		null
	);

	internal static CacheReadReceipt FromLayer(int layerIndex, ICacheLayer layer, CacheValueFreshness freshness)
	{
		return new CacheReadReceipt(
			layerIndex == 0 ? CacheValueSource.MemoryLayer : CacheValueSource.BackingLayer,
			freshness,
			layerIndex,
			layer.GetType()
		);
	}
}

/// <summary>
/// The result of a cache read and a receipt that describes its source without modifying the value.
/// </summary>
/// <typeparam name="T">The type of value that was read.</typeparam>
/// <param name="Value">The value returned to the caller.</param>
/// <param name="Receipt">The source receipt for this read.</param>
public sealed record CacheReadResult<T>(T? Value, CacheReadReceipt Receipt);
