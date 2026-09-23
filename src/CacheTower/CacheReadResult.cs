namespace CacheTower;

/// <summary>
/// The result of a cache read together with a receipt describing where the value was served from.
/// </summary>
/// <remarks>
/// The receipt (<see cref="Source"/> and <see cref="LayerIndex"/>) is diagnostic only - it never changes the <see cref="Value"/>.
/// </remarks>
/// <typeparam name="T">The type of value in the cache entry.</typeparam>
/// <param name="Value">The value that was read.</param>
/// <param name="Source">Where the value was served from.</param>
/// <param name="LayerIndex">
/// The index of the cache layer that served the value, or <c>-1</c> when the value did not come from a cache layer
/// (for example when it was produced by the value factory).
/// </param>
public readonly record struct CacheReadResult<T>(T? Value, CacheValueSource Source, int LayerIndex);
