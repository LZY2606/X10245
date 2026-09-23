namespace CacheTower;

/// <summary>
/// The result of a cache read via
/// <see cref="ICacheStack.GetOrSetWithReceiptAsync{T}(string, System.Func{T, System.Threading.Tasks.Task{T}}, CacheSettings, System.Threading.CancellationToken)"/>.
/// Combines the cached value with a source receipt describing where the value came from.
/// </summary>
/// <remarks>
/// The receipt is diagnostic only - it never changes the <see cref="Value"/>.
/// </remarks>
/// <typeparam name="T">The type of value in the cache entry.</typeparam>
/// <param name="Value">The cached (or refreshed) value.</param>
/// <param name="Source">The source receipt for the value.</param>
public readonly record struct CacheReadResult<T>(T? Value, CacheValueSource Source);
