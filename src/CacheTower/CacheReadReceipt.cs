namespace CacheTower;

/// <summary>
/// Describes where the value returned by a cache read came from.
/// </summary>
public enum CacheReadOrigin
{
	/// <summary>
	/// The value was produced by the value factory.
	/// </summary>
	Factory = 0,
	/// <summary>
	/// The value came from the first cache layer.
	/// </summary>
	MemoryLayer = 1,
	/// <summary>
	/// The value came from a cache layer after the first cache layer.
	/// </summary>
	BackingLayer = 2
}

/// <summary>
/// Describes whether a returned cache value was fresh or stale.
/// </summary>
public enum CacheReadStatus
{
	/// <summary>
	/// The value was fresh when it was returned.
	/// </summary>
	Fresh = 0,
	/// <summary>
	/// The value was stale when it was returned.
	/// </summary>
	Stale = 1
}

/// <summary>
/// Describes the source of a value without changing that value.
/// </summary>
/// <param name="Origin">Whether the value came from the factory, the first layer or a backing layer.</param>
/// <param name="Status">Whether the returned value was fresh or stale.</param>
/// <param name="CacheLayerIndex">The zero-based cache layer index, or <see langword="null"/> when the value came from the factory.</param>
public readonly record struct CacheReadReceipt(
	CacheReadOrigin Origin,
	CacheReadStatus Status,
	int? CacheLayerIndex
)
{
	/// <summary>
	/// Creates a receipt for a value produced by the factory.
	/// </summary>
	public static CacheReadReceipt Factory { get; } = new(CacheReadOrigin.Factory, CacheReadStatus.Fresh, null);
}

/// <summary>
/// The value returned by a cache read and a receipt describing where that value came from.
/// </summary>
/// <typeparam name="T">The type of the cached value.</typeparam>
/// <param name="Value">The cached value.</param>
/// <param name="Receipt">The source receipt for this read.</param>
public readonly record struct CacheReadResult<T>(T? Value, CacheReadReceipt Receipt);
