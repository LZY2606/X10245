namespace CacheTower;

/// <summary>
/// Describes where the value returned by a cache read came from.
/// This forms the provenance receipt of a <see cref="CacheReadResult{T}"/> and never alters the value itself.
/// </summary>
public enum CacheValueSource
{
	/// <summary>
	/// An unexpired entry served from the first (highest priority, typically in-memory) cache layer.
	/// </summary>
	MemoryLayer,
	/// <summary>
	/// An unexpired entry served from a lower (backing) cache layer.
	/// </summary>
	BackingLayer,
	/// <summary>
	/// An expired entry served because it is still within the stale-while-revalidate window.
	/// A single background refresh is triggered for the cache key.
	/// </summary>
	Stale,
	/// <summary>
	/// A fresh value produced by a concurrent shared refresh that this caller waited on.
	/// This caller's value factory was not invoked.
	/// </summary>
	Fresh,
	/// <summary>
	/// A value produced by this caller's own value factory.
	/// </summary>
	Factory
}
