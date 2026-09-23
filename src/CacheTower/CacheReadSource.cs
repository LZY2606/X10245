namespace CacheTower;

/// <summary>
/// Describes the source that produced the value returned to the caller.
/// </summary>
public enum CacheReadSource
{
	/// <summary>
	/// The value was read directly from a cache layer.
	/// </summary>
	CacheLayer = 0,
	/// <summary>
	/// The value was produced by the value factory.
	/// </summary>
	ValueFactory = 1
}
