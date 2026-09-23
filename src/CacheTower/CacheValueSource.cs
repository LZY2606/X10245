namespace CacheTower;

/// <summary>
/// Describes where the value of a cache read was served from.
/// </summary>
/// <remarks>
/// The source is a diagnostic receipt for a read - it never changes the value that was returned.
/// </remarks>
public enum CacheValueSource
{
	/// <summary>
	/// The value was served unexpired from the first (top-most) cache layer, typically an in-memory layer.
	/// </summary>
	MemoryLayer,
	/// <summary>
	/// The value was served unexpired from a lower (fallback) cache layer.
	/// </summary>
	FallbackLayer,
	/// <summary>
	/// The value was served stale - either past its stale date or expired but still inside
	/// the <see cref="CacheSettings.StaleWhileRevalidateWindow"/> - while a background refresh was triggered.
	/// </summary>
	Stale,
	/// <summary>
	/// The value was produced by a refresh shared with other callers that this caller waited on.
	/// </summary>
	Fresh,
	/// <summary>
	/// The value was produced by the value factory invoked for this caller.
	/// </summary>
	Factory
}
