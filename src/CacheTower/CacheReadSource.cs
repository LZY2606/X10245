namespace CacheTower;

/// <summary>
/// Describes where the value of a cache read actually came from.
/// </summary>
/// <remarks>
/// The source is reported through <see cref="CacheReadReceipt"/> and never changes the returned value itself.
/// </remarks>
public enum CacheReadSource
{
	/// <summary>
	/// The source could not be determined. This is the default value and is never reported for a successful read.
	/// </summary>
	Unknown = 0,
	/// <summary>
	/// An unexpired entry served from the first (highest priority, typically in-memory) cache layer.
	/// </summary>
	MemoryLayer,
	/// <summary>
	/// An unexpired entry served from a lower (fallback/backing) cache layer.
	/// </summary>
	FallbackLayer,
	/// <summary>
	/// An expired entry served within the opt-in stale-while-revalidate window while a refresh is triggered in the background.
	/// </summary>
	Stale,
	/// <summary>
	/// A refreshed entry produced by a shared (single-flight) refresh that this caller waited on but did not itself execute.
	/// </summary>
	Fresh,
	/// <summary>
	/// An entry produced by the value factory executed by this caller.
	/// </summary>
	Factory
}
