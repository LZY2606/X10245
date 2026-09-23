namespace CacheTower;

/// <summary>
/// Describes where the value of a cache read came from.
/// </summary>
/// <remarks>
/// The numeric values are part of the public contract and must remain stable for diagnostics.
/// </remarks>
public enum CacheReadSource
{
	/// <summary>
	/// The source of the value could not be determined. This is returned explicitly instead of guessing a successful source.
	/// </summary>
	Unknown = 0,
	/// <summary>
	/// The value was an unexpired cache entry served by the first (memory) cache layer.
	/// </summary>
	MemoryLayer = 1,
	/// <summary>
	/// The value was an unexpired cache entry served by a lower (backing) cache layer.
	/// </summary>
	BackingLayer = 2,
	/// <summary>
	/// The value was an expired cache entry served within the stale-while-revalidate window while a refresh is triggered in the background.
	/// </summary>
	Stale = 3,
	/// <summary>
	/// The value came from the cache after a shared refresh completed, rather than from this caller's value factory.
	/// </summary>
	Fresh = 4,
	/// <summary>
	/// The value was produced by this caller's value factory invocation.
	/// </summary>
	Factory = 5
}
