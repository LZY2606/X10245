namespace CacheTower;

/// <summary>
/// The receipt of a cache read performed by <see cref="ICacheStack.GetOrSetWithReceiptAsync{T}(string, Func{T, Task{T}}, CacheSettings, System.Threading.CancellationToken)"/>.
/// It pairs the returned value with the <see cref="CacheSource"/> it came from without altering the value itself.
/// </summary>
/// <typeparam name="T">The type of value in the cache entry.</typeparam>
/// <param name="Value">The value returned for the cache read.</param>
/// <param name="Source">Where the value came from.</param>
public sealed record CacheReadReceipt<T>(T? Value, CacheSource Source);
