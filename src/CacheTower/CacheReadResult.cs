namespace CacheTower;

/// <summary>
/// The result of a cache read via <see cref="ICacheStack.GetOrSetWithReceiptAsync{T}(string, System.Func{T, System.Threading.Tasks.Task{T}}, CacheSettings, StaleWhileRevalidateSettings?, System.Threading.CancellationToken)"/>,
/// pairing the value with a provenance receipt.
/// </summary>
/// <remarks>
/// The receipt describes where the value came from; it never alters the value itself.
/// </remarks>
/// <typeparam name="T">The type of value in the cache entry.</typeparam>
/// <param name="Value">The cached or generated value.</param>
/// <param name="Source">The provenance of <paramref name="Value"/>.</param>
public sealed record CacheReadResult<T>(T? Value, CacheReadSource Source);
