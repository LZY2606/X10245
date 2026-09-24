namespace CacheTower;

/// <summary>
/// Describes where the value of a cache read came from.
/// </summary>
/// <remarks>
/// The source is reported by <see cref="ICacheStack.GetOrSetWithReceiptAsync{T}(string, System.Func{T, System.Threading.Tasks.Task{T}}, CacheSettings, System.Threading.CancellationToken)"/>
/// as a receipt for the read. It is purely diagnostic and never changes the returned value.
/// </remarks>
public enum CacheReadSource
{
	/// <summary>
	/// The value was found unexpired in the first (local/memory) cache layer.
	/// </summary>
	MemoryLayer,
	/// <summary>
	/// The value was found unexpired in a lower (fallback) cache layer.
	/// </summary>
	FallbackLayer,
	/// <summary>
	/// The value had expired but was still inside the stale-while-revalidate window
	/// (<see cref="CacheSettings.StaleWhileRevalidateWindow"/>) and was returned while a refresh runs in the background.
	/// </summary>
	Stale,
	/// <summary>
	/// The value was produced by a shared refresh that this read did not itself run
	/// (for example, another concurrent caller ran the value factory).
	/// </summary>
	Fresh,
	/// <summary>
	/// The value was produced by the value factory during this read.
	/// </summary>
	Factory
}
