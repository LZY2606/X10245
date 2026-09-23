using System;

namespace CacheTower;

/// <summary>
/// Describes the freshness of the value returned from a cache read.
/// </summary>
public enum CacheReadFreshness
{
	/// <summary>
	/// The value was returned from a cache entry that had not reached its stale date.
	/// </summary>
	Fresh,
	/// <summary>
	/// The value was returned from a stale cache entry.
	/// With stale-while-revalidate, this can include an entry that has expired but is still within the configured stale-while-revalidate window.
	/// </summary>
	Stale,
}

/// <summary>
/// Describes where the value returned from a cache read came from.
/// </summary>
public enum CacheReadTier
{
	/// <summary>
	/// The value was produced by the value factory because no usable cache entry was available.
	/// </summary>
	Factory,
	/// <summary>
	/// The value was found in the first (highest priority) cache layer.
	/// </summary>
	MemoryLayer,
	/// <summary>
	/// The value was found in a cache layer other than the first (for example, a file or distributed fallback layer).
	/// </summary>
	BackingLayer,
}

/// <summary>
/// A receipt describing the source of a value returned by a cache read.
/// </summary>
/// <remarks>
/// Receipts are diagnostic metadata only - they never affect, clone or replace the value returned to the caller.
/// <para>
/// The combination of <see cref="Tier"/> and <see cref="Freshness"/> distinguishes between the possible sources:
/// a fresh or stale value from the memory layer, a fresh or stale value from a backing layer and a freshly generated factory value.
/// </para>
/// </remarks>
/// <param name="Tier">Where the returned value came from.</param>
/// <param name="Freshness">Whether the returned value was fresh or stale.</param>
/// <param name="LayerIndex">The zero-based index of the cache layer the value came from, or <c>-1</c> when <see cref="Tier"/> is <see cref="CacheReadTier.Factory"/>.</param>
public readonly record struct CacheReadReceipt(CacheReadTier Tier, CacheReadFreshness Freshness, int LayerIndex)
{
	/// <summary>
	/// Creates a receipt for a value produced by the value factory.
	/// </summary>
	public static CacheReadReceipt FromFactory() => new(CacheReadTier.Factory, CacheReadFreshness.Fresh, -1);

	/// <summary>
	/// Creates a receipt for a value found in a cache layer.
	/// </summary>
	/// <param name="layerIndex">The zero-based index of the cache layer that provided the value.</param>
	/// <param name="freshness">Whether the found value was fresh or stale.</param>
	public static CacheReadReceipt FromLayer(int layerIndex, CacheReadFreshness freshness)
	{
		var tier = layerIndex == 0 ? CacheReadTier.MemoryLayer : CacheReadTier.BackingLayer;
		return new CacheReadReceipt(tier, freshness, layerIndex);
	}

	/// <inheritdoc/>
	public override string ToString() => Tier == CacheReadTier.Factory
		? nameof(CacheReadTier.Factory)
		: $"{Tier}(Index={LayerIndex})/{Freshness}";
}

/// <summary>
/// The result of a cache read, containing both the value and a <see cref="CacheReadReceipt"/> describing where it came from.
/// </summary>
/// <typeparam name="T">The type of the cached value.</typeparam>
/// <param name="Value">The value returned for the cache key. Receipts never alter this value.</param>
/// <param name="Receipt">The receipt describing the source of <paramref name="Value"/>.</param>
public readonly record struct CacheReadResult<T>(T Value, CacheReadReceipt Receipt)
{
	/// <summary>
	/// Supports deconstruction into a value and receipt pair.
	/// </summary>
	public void Deconstruct(out T value, out CacheReadReceipt receipt)
	{
		value = Value;
		receipt = Receipt;
	}
}
