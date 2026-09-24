namespace CacheTower;

/// <summary>
/// The outcome of a cache read: the value itself and a receipt describing the source it was served from.
/// </summary>
/// <remarks>
/// The source receipt is informational only - it never alters the <paramref name="Value"/>.
/// </remarks>
/// <typeparam name="T">The type of value that was read.</typeparam>
/// <param name="Value">The value that was read.</param>
/// <param name="Source">The source receipt describing where the value was served from.</param>
public readonly record struct CacheFetchResult<T>(T? Value, CacheValueSource Source)
{
	/// <summary>
	/// Whether the value was served from a cache layer rather than produced by the value factory.
	/// </summary>
	public bool IsFromCache => Source is CacheValueSource.MemoryLayerFresh or CacheValueSource.MemoryLayerStale
		or CacheValueSource.FallbackLayerFresh or CacheValueSource.FallbackLayerStale;
}
