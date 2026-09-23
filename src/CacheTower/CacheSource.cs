namespace CacheTower;

/// <summary>
/// Identifies where the value of a cache read came from.
/// </summary>
/// <remarks>
/// The source is reported by <see cref="ICacheStack.GetOrSetWithReceiptAsync{T}(string, Func{T, Task{T}}, CacheSettings, System.Threading.CancellationToken)"/>
/// as part of the <see cref="CacheReadReceipt{T}"/>. It describes the origin of the returned value; it never changes the value itself.
/// </remarks>
public enum CacheSource
{
	/// <summary>
	/// An unexpired entry served from the topmost cache layer (for example, an in-memory layer).
	/// </summary>
	MemoryLayer,
	/// <summary>
	/// An unexpired entry served from a lower-priority (fallback) cache layer.
	/// </summary>
	FallbackLayer,
	/// <summary>
	/// An expired entry served within the <see cref="CacheSettings.StaleWhileRevalidateWindow"/> while a background refresh is in flight.
	/// </summary>
	Stale,
	/// <summary>
	/// A value newly produced by the value factory through a foreground refresh of an entry that had expired beyond the stale-while-revalidate window.
	/// </summary>
	Fresh,
	/// <summary>
	/// A value newly produced by the value factory because no cache entry existed (a cache miss).
	/// </summary>
	Factory
}
