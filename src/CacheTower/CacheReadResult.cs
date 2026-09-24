namespace CacheTower;

/// <summary>
/// The result of an opt-in stale-while-revalidate read, pairing the cached value with a <see cref="CacheReadSource"/> receipt.
/// </summary>
/// <remarks>
/// The receipt describes where <see cref="Value"/> came from; it never modifies the value itself.
/// </remarks>
/// <typeparam name="T">The type of value in the cache entry.</typeparam>
/// <param name="Value">The value that was read.</param>
/// <param name="Source">Where the value came from.</param>
public readonly record struct CacheReadResult<T>(T? Value, CacheReadSource Source);
