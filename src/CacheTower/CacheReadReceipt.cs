namespace CacheTower;

/// <summary>
/// The result of a cache read together with a receipt describing where the value came from.
/// </summary>
/// <remarks>
/// The receipt is purely diagnostic; it never changes the <see cref="Value"/> that was read.
/// </remarks>
/// <typeparam name="T">The type of value that was read.</typeparam>
/// <param name="Value">The value that was read.</param>
/// <param name="Source">Where the value came from.</param>
public sealed record CacheReadReceipt<T>(T? Value, CacheReadSource Source);
