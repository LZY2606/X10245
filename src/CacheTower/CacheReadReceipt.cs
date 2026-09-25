namespace CacheTower;

/// <summary>
/// Describes where the value of a cache read came from.
/// </summary>
public enum CacheValueSource
{
	/// <summary>
	/// The value was served unexpired from the top-most cache layer (for example, a local memory layer).
	/// </summary>
	MemoryLayer,
	/// <summary>
	/// The value was served unexpired from a lower (fallback) cache layer.
	/// </summary>
	FallbackLayer,
	/// <summary>
	/// The value was served expired, inside the opt-in stale-while-revalidate window, while a single background refresh was triggered for the key.
	/// </summary>
	Stale,
	/// <summary>
	/// The value was produced by a refresh shared with another caller (single-flight); this caller did not invoke the value factory itself.
	/// </summary>
	Fresh,
	/// <summary>
	/// The value was produced by the value factory during this call.
	/// </summary>
	Factory
}

/// <summary>
/// The receipt of a cache read via <see cref="ICacheStack.GetOrSetWithReceiptAsync{T}(string, System.Func{T, System.Threading.Tasks.Task{T}}, CacheSettings, System.Threading.CancellationToken)"/>.
/// The receipt only describes the read - the <see cref="Value"/> itself is exactly what a plain read would have returned.
/// </summary>
/// <typeparam name="T">The type of value in the cache entry.</typeparam>
/// <param name="Value">The value that was read, unchanged by the receipt.</param>
/// <param name="Source">Where the value came from.</param>
public readonly record struct CacheReadReceipt<T>(T? Value, CacheValueSource Source)
{
	/// <summary>
	/// Whether the served value was expired and served inside the stale-while-revalidate window.
	/// </summary>
	public bool IsStale => Source == CacheValueSource.Stale;
}
