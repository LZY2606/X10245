namespace CacheTower;

/// <summary>
/// The result of a cache read performed with a stale-while-revalidate policy,
/// pairing the value with a <see cref="CacheReadReceipt"/> that describes where it came from.
/// </summary>
/// <typeparam name="T">The type of value in the cache read.</typeparam>
/// <param name="Value">The cached or generated value.</param>
/// <param name="Receipt">The receipt describing the origin and freshness of the value.</param>
public readonly record struct CacheReadResult<T>(T? Value, CacheReadReceipt Receipt);
