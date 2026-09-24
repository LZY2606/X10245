namespace CacheTower;

/// <summary>
/// The result of a cache read that carries a provenance receipt.
/// The receipt describes where the value came from without changing the value itself.
/// </summary>
/// <typeparam name="T">The type of value in the cache entry.</typeparam>
/// <param name="Value">The value returned for the cache key.</param>
/// <param name="Source">The provenance of <paramref name="Value"/>.</param>
public readonly record struct CacheReadResult<T>(T? Value, CacheValueSource Source)
{
	/// <summary>
	/// The value returned for the cache key.
	/// </summary>
	public T? Value { get; } = Value;

	/// <summary>
	/// The provenance receipt for this read - where <see cref="Value"/> came from.
	/// </summary>
	public CacheValueSource Source { get; } = Source;
}
