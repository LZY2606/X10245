namespace CacheTower;

/// <summary>
/// Describes where the value of a cache read came from.
/// </summary>
/// <remarks>
/// The source is reported alongside the value (see <see cref="CacheReadResult{T}"/>) and never changes the value itself.
/// </remarks>
public enum CacheReadSource
{
	/// <summary>
	/// The source of the value could not be determined.
	/// This is reported explicitly instead of guessing a successful source.
	/// </summary>
	Unknown = 0,
	/// <summary>
	/// The value was served fresh (not expired) from the first cache layer.
	/// </summary>
	MemoryLayer,
	/// <summary>
	/// The value was served fresh (not expired) from a lower-priority fallback cache layer.
	/// </summary>
	FallbackLayer,
	/// <summary>
	/// The value was expired but still inside the stale-while-revalidate window, so it was served while a refresh was triggered.
	/// </summary>
	Stale,
	/// <summary>
	/// The value came from a shared refresh that was already in flight (single-flight); this caller did not run the value factory itself.
	/// </summary>
	Fresh,
	/// <summary>
	/// The value was produced by the caller's own value factory during this read.
	/// </summary>
	Factory
}
