using System;

namespace CacheTower;

/// <summary>
/// A receipt describing the origin of a cache read. The receipt is purely informational and never changes the returned value.
/// </summary>
public readonly record struct CacheReadReceipt
{
	/// <summary>
	/// Where the value of the read came from.
	/// </summary>
	public CacheReadSource Source { get; }

	/// <summary>
	/// The index of the cache layer the value was served from or <c>-1</c> when the value did not come from a cache layer.
	/// </summary>
	public int LayerIndex { get; }

	/// <summary>
	/// The expiry of the cache entry the value was served from or <see langword="null"/> when the value did not come from an existing cache entry.
	/// </summary>
	public DateTime? EntryExpiry { get; }

	/// <summary>
	/// Creates a new <see cref="CacheReadReceipt"/>.
	/// </summary>
	/// <param name="source">Where the value of the read came from.</param>
	/// <param name="layerIndex">The index of the cache layer the value was served from or <c>-1</c>.</param>
	/// <param name="entryExpiry">The expiry of the cache entry the value was served from or <see langword="null"/>.</param>
	public CacheReadReceipt(CacheReadSource source, int layerIndex, DateTime? entryExpiry)
	{
		Source = source;
		LayerIndex = layerIndex;
		EntryExpiry = entryExpiry;
	}
}
