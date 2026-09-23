using System;

namespace CacheTower;

/// <summary>
/// Describes where the value of a cache read came from.
/// </summary>
/// <remarks>
/// The order and values of this enum are part of the public contract and must remain stable.
/// </remarks>
public enum CacheReadSource
{
	/// <summary>
	/// The source of the value could not be determined. No value should be assumed to be available.
	/// </summary>
	Unknown = 0,
	/// <summary>
	/// The value was served fresh from the top-most (local, typically in-memory) cache layer.
	/// </summary>
	MemoryLayer = 1,
	/// <summary>
	/// The value was served fresh from a lower (backstop) cache layer.
	/// </summary>
	BackstopLayer = 2,
	/// <summary>
	/// The value had expired but was served within the stale-while-revalidate window while a refresh is triggered in the background.
	/// </summary>
	Stale = 3,
	/// <summary>
	/// The value was produced by a shared (single-flight) refresh coordinated for the same cache key, rather than by this caller's own factory invocation.
	/// </summary>
	Fresh = 4,
	/// <summary>
	/// The value was produced by this caller's own value factory invocation.
	/// </summary>
	Factory = 5
}

/// <summary>
/// A receipt describing the origin of a cache read. The receipt is informational only and never changes the returned value.
/// </summary>
/// <param name="Source">The source the value was served from.</param>
/// <param name="LayerIndex">
/// The index of the cache layer the value was served from, or <c>-1</c> when no cache layer was involved
/// (for example when the value came from the value factory or the source is <see cref="CacheReadSource.Unknown"/>).
/// </param>
public readonly record struct CacheReadReceipt(CacheReadSource Source, int LayerIndex)
{
	/// <summary>
	/// A receipt for when the source of a read cannot be determined.
	/// </summary>
	public static CacheReadReceipt Unknown { get; } = new(CacheReadSource.Unknown, -1);
}

/// <summary>
/// The result of a cache read, pairing the value with a <see cref="CacheReadReceipt"/> that describes where the value came from.
/// </summary>
/// <typeparam name="T">The type of value in the cache entry.</typeparam>
/// <param name="Value">The value for the cache key. This is identical to the value returned by <see cref="ICacheStack.GetOrSetAsync{T}(string, Func{T, System.Threading.Tasks.Task{T}}, CacheSettings)"/>.</param>
/// <param name="Receipt">The receipt describing the source of the value.</param>
public readonly record struct CacheReadResult<T>(T? Value, CacheReadReceipt Receipt);
