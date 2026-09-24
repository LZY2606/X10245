using System;

namespace CacheTower;

/// <summary>
/// A provenance receipt for a cache read, describing where the returned value came from.
/// </summary>
/// <remarks>
/// The receipt is purely diagnostic: it never changes the returned value.
/// </remarks>
public readonly record struct CacheReadReceipt
{
	/// <summary>
	/// Where the value came from.
	/// </summary>
	public CacheReadSource Source { get; }

	/// <summary>
	/// The index of the cache layer the entry was read from, or <c>-1</c> when the value did not come from a cache layer read
	/// (for example when it was produced by the value factory).
	/// </summary>
	public int LayerIndex { get; }

	/// <summary>
	/// The expiry of the cache entry that the value belongs to, when known.
	/// </summary>
	public DateTime? Expiry { get; }

	/// <summary>
	/// Creates a new <see cref="CacheReadReceipt"/>.
	/// </summary>
	/// <param name="source">Where the value came from.</param>
	/// <param name="layerIndex">The index of the cache layer the entry was read from, or <c>-1</c> when not read from a layer.</param>
	/// <param name="expiry">The expiry of the cache entry that the value belongs to, when known.</param>
	public CacheReadReceipt(CacheReadSource source, int layerIndex, DateTime? expiry)
	{
		Source = source;
		LayerIndex = layerIndex;
		Expiry = expiry;
	}
}
