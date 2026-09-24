namespace CacheTower;

/// <summary>
/// Identifies the source that provided the value of a cache read.
/// </summary>
/// <remarks>
/// The "memory layer" refers to cache layers implementing <see cref="ILocalCacheLayer"/> (such as <see cref="Providers.Memory.MemoryCacheLayer"/>);
/// the "fallback layer" refers to any other (backing) cache layer in the cache stack.
/// </remarks>
public enum CacheValueSource
{
	/// <summary>
	/// The source of the value could not be determined.
	/// </summary>
	Unknown = 0,
	/// <summary>
	/// The value was produced by the value factory because no usable cache entry was available.
	/// </summary>
	Factory = 1,
	/// <summary>
	/// The value was served fresh (within its stale window) from a local, in-memory cache layer.
	/// </summary>
	MemoryLayerFresh = 2,
	/// <summary>
	/// The value was served stale (past its stale date, or expired but within the stale-while-revalidate window) from a local, in-memory cache layer.
	/// </summary>
	MemoryLayerStale = 3,
	/// <summary>
	/// The value was served fresh (within its stale window) from a fallback (backing) cache layer.
	/// </summary>
	FallbackLayerFresh = 4,
	/// <summary>
	/// The value was served stale (past its stale date, or expired but within the stale-while-revalidate window) from a fallback (backing) cache layer.
	/// </summary>
	FallbackLayerStale = 5
}
