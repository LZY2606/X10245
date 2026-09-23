namespace CacheTower;

/// <summary>
/// Describes where the value of a cache read came from.
/// </summary>
/// <remarks>
/// The source is reported by <see cref="CacheStack.GetOrSetWithReceiptAsync{T}(string, Func{T, System.Threading.Tasks.Task{T}}, CacheSettings, System.Threading.CancellationToken)"/>
/// as part of a <see cref="CacheReadReceipt{T}"/>. It is purely diagnostic - the value itself is never altered by the receipt.
/// </remarks>
public enum CacheValueSource
{
	/// <summary>
	/// An unexpired value served from the top-most cache layer (typically the in-memory layer).
	/// </summary>
	MemoryLayer,
	/// <summary>
	/// An unexpired value served from a lower-priority fallback cache layer.
	/// </summary>
	FallbackLayer,
	/// <summary>
	/// An expired value served inside the opt-in stale-while-revalidate window
	/// (<see cref="CacheSettings.StaleWhileRevalidateWindow"/>) while a single background refresh is triggered.
	/// </summary>
	Stale,
	/// <summary>
	/// A newly refreshed value that was produced by a shared refresh completed by another concurrent caller.
	/// </summary>
	Fresh,
	/// <summary>
	/// A value produced by this call's own value factory.
	/// </summary>
	Factory
}
