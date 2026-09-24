namespace CacheTower;

/// <summary>
/// Describes where the value of a cache read originated from.
/// </summary>
public enum CacheValueOrigin
{
	/// <summary>
	/// The value was served by the first (highest priority, typically in-memory) cache layer.
	/// </summary>
	LocalLayer,
	/// <summary>
	/// The value was served by a fallback (lower priority) cache layer.
	/// </summary>
	FallbackLayer,
	/// <summary>
	/// The value was produced by the value factory during this refresh cycle.
	/// Concurrent callers may share the same single-flight factory refresh.
	/// </summary>
	Factory
}

/// <summary>
/// Describes whether the value of a cache read was fresh or stale.
/// </summary>
public enum CacheValueFreshness
{
	/// <summary>
	/// The value was within its configured lifetime when it was read.
	/// </summary>
	Fresh,
	/// <summary>
	/// The value had expired but was still inside the stale-while-revalidate window when it was read.
	/// </summary>
	Stale
}

/// <summary>
/// A receipt describing the origin and freshness of a cache read.
/// The receipt is metadata only; it never changes the returned value.
/// </summary>
/// <param name="Origin">Where the value originated from.</param>
/// <param name="Freshness">Whether the value was fresh or stale when read.</param>
public readonly record struct CacheReadReceipt(CacheValueOrigin Origin, CacheValueFreshness Freshness);
