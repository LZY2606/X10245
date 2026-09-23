namespace CacheTower;

/// <summary>
/// Describes where the value returned by
/// <see cref="ICacheStack.GetOrSetWithReceiptAsync{T}(string, System.Func{T, System.Threading.Tasks.Task{T}}, CacheSettings, System.Threading.CancellationToken)"/>
/// was sourced from.
/// </summary>
/// <remarks>
/// The underlying values are part of the public contract and are stable - they must not be reordered or renumbered.
/// </remarks>
public enum CacheValueSource
{
	/// <summary>
	/// The value was a non-expired hit on the first (highest priority) cache layer, for example the memory layer.
	/// </summary>
	MemoryLayer = 0,
	/// <summary>
	/// The value was a non-expired hit on a lower (fallback) cache layer.
	/// </summary>
	FallbackLayer = 1,
	/// <summary>
	/// The value was expired but still within the <see cref="CacheSettings.StaleWhileRevalidateWindow"/> and was returned while a background refresh runs.
	/// </summary>
	Stale = 2,
	/// <summary>
	/// The value came from a refresh that this call did not run itself - it waited on the single-flight refresh for the cache key and read the refreshed entry.
	/// </summary>
	Fresh = 3,
	/// <summary>
	/// The value was produced by the value factory invoked by this call.
	/// </summary>
	Factory = 4
}
