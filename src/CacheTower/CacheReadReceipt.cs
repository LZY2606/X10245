namespace CacheTower;

/// <summary>
/// Describes where the value of a cache read was sourced from.
/// </summary>
public enum CacheReadSource
{
	/// <summary>
	/// The source of the value could not be determined.
	/// </summary>
	Unknown = 0,
	/// <summary>
	/// The value was served by the first (top-most) cache layer, typically an in-memory layer.
	/// </summary>
	MemoryLayer,
	/// <summary>
	/// The value was served by a fallback (lower priority) cache layer.
	/// </summary>
	FallbackLayer,
	/// <summary>
	/// The value was produced by the value factory during a refresh.
	/// </summary>
	Factory
}

/// <summary>
/// A provenance receipt for a cache read, describing where the value came from and whether it was stale.
/// </summary>
/// <remarks>
/// The receipt is purely informational - it never changes the value that was read.
/// </remarks>
/// <param name="Source">Where the value was sourced from.</param>
/// <param name="IsStale">Whether the value was stale (past its stale date) or expired-but-served when returned.</param>
public readonly record struct CacheReadReceipt(CacheReadSource Source, bool IsStale);

/// <summary>
/// The result of a cache read, pairing the value with its <see cref="CacheReadReceipt"/>.
/// </summary>
/// <typeparam name="T">The type of value that was read.</typeparam>
/// <param name="Value">The value that was read. This is the same value a receipt-less read would return.</param>
/// <param name="Receipt">The provenance receipt for the read.</param>
public readonly record struct CacheReadResult<T>(T? Value, CacheReadReceipt Receipt);
