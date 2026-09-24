namespace CacheTower;

/// <summary>
/// Describes where the value of a cache read came from.
/// </summary>
public enum CacheReadSource
{
	/// <summary>
	/// The value was served unexpired from the first (highest priority, typically in-memory) cache layer.
	/// </summary>
	MemoryLayer,
	/// <summary>
	/// The value was served unexpired from a lower priority (fallback) cache layer.
	/// </summary>
	FallbackLayer,
	/// <summary>
	/// The value was served expired but still inside the stale-while-revalidate window while a background refresh is in flight.
	/// </summary>
	Stale,
	/// <summary>
	/// The value came from a shared refresh that was completed by another caller (single-flight); this caller did not run the value factory itself.
	/// </summary>
	Fresh,
	/// <summary>
	/// The value was produced by the value factory of this call because no usable cache entry was available.
	/// </summary>
	Factory
}
