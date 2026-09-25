using System;

namespace CacheTower;

/// <summary>
/// Describes where the value returned by a cache read originated.
/// </summary>
/// <remarks>
/// The source never affects the value returned - it is a diagnostic receipt only.
/// </remarks>
public enum CacheValueSource
{
	/// <summary>
	/// The value was served from the first (highest priority) cache layer, for example an in-memory layer.
	/// </summary>
	MemoryLayer,
	/// <summary>
	/// The value was served from a cache layer below the first, for example a file or distributed layer.
	/// </summary>
	BackingLayer,
	/// <summary>
	/// The value was produced by the value factory because no servable cache entry existed.
	/// </summary>
	Factory
}

/// <summary>
/// Describes how fresh the value returned by a cache read was.
/// </summary>
public enum CacheValueFreshness
{
	/// <summary>
	/// The served entry had not expired.
	/// </summary>
	Fresh,
	/// <summary>
	/// The served entry had expired but was still within the opted-in stale-while-revalidate window.
	/// </summary>
	Stale
}

/// <summary>
/// A receipt describing the provenance of a value returned from a cache read.
/// </summary>
/// <remarks>
/// <para>
/// The receipt is produced alongside every read and never changes the value that was returned.
/// </para>
/// <para>
/// <see cref="Source"/> distinguishes the memory layer, a backing layer and the value factory while
/// <see cref="Freshness"/> distinguishes fresh and stale entries. For cached values, <see cref="LayerIndex"/>
/// is the index of the layer within the configured stack (zero being the first layer).
/// </para>
/// </remarks>
/// <param name="Source">Where the value originated.</param>
/// <param name="Freshness">Whether the value was fresh or stale. This is <see langword="null"/> when the value came from the <see cref="CacheValueSource.Factory"/>.</param>
/// <param name="LayerIndex">The index of the layer that served the value, or <c>-1</c> when the value came from the factory.</param>
/// <param name="EntryExpiry">The expiry of the entry that was served, or <see langword="null"/> when the value came from the factory.</param>
public sealed record CacheReadReceipt(
	CacheValueSource Source,
	CacheValueFreshness? Freshness,
	int LayerIndex,
	DateTime? EntryExpiry
)
{
	/// <summary>
	/// Creates a receipt for a value served from a cache layer.
	/// </summary>
	/// <param name="layerIndex">The index of the layer within the configured stack.</param>
	/// <param name="expiry">The expiry of the served entry.</param>
	/// <param name="stale"><see langword="true"/> when the served entry was stale; otherwise <see langword="false"/> for a fresh entry.</param>
	/// <returns></returns>
	public static CacheReadReceipt ForCacheLayer(int layerIndex, DateTime expiry, bool stale) => new(
		layerIndex == 0 ? CacheValueSource.MemoryLayer : CacheValueSource.BackingLayer,
		stale ? CacheValueFreshness.Stale : CacheValueFreshness.Fresh,
		layerIndex,
		expiry
	);

	/// <summary>
	/// Creates a receipt for a value produced by the value factory.
	/// </summary>
	/// <returns></returns>
	public static CacheReadReceipt ForFactory() => new(CacheValueSource.Factory, null, -1, null);

	/// <inheritdoc/>
	public override string ToString() =>
		$"{Source}/{(Freshness.HasValue ? Freshness.Value.ToString() : "n/a")}/Layer={LayerIndex}";
}

/// <summary>
/// The result of a cache read: the value and the <see cref="Receipt"/> describing where it came from.
/// </summary>
/// <typeparam name="T">The type of the cached value.</typeparam>
/// <param name="Value">The value that was read. This is the same value that would be returned without a receipt.</param>
/// <param name="Receipt">The provenance receipt for <paramref name="Value"/>.</param>
public sealed record CacheReadResult<T>(T? Value, CacheReadReceipt Receipt)
{
	/// <summary>
	/// Implicitly unwraps the read result to its value, allowing receipt-aware results to be consumed as plain values.
	/// </summary>
	/// <param name="result">The read result to unwrap.</param>
	public static implicit operator T?(CacheReadResult<T> result) => result is null ? default : result.Value;
}
