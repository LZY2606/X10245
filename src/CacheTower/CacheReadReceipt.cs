namespace CacheTower;

/// <summary>
/// The result of a cache read together with a receipt describing where the value came from.
/// </summary>
/// <remarks>
/// The receipt is diagnostic only - <see cref="Value"/> is never modified by it.
/// </remarks>
/// <typeparam name="T">The type of the cached value.</typeparam>
/// <param name="Value">The value returned to the caller.</param>
/// <param name="Source">Where the value came from.</param>
public readonly record struct CacheReadReceipt<T>(T? Value, CacheValueSource Source);
