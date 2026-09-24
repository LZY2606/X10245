using System;

namespace CacheTower;

/// <summary>
/// Describes where the value of a cache read was served from.
/// </summary>
public enum CacheFetchSource
{
	/// <summary>
	/// A fresh (unexpired) value served from a local cache layer, such as an in-memory layer.
	/// </summary>
	MemoryLayer,
	/// <summary>
	/// A fresh (unexpired) value served from a fallback (non-local) cache layer.
	/// </summary>
	FallbackLayer,
	/// <summary>
	/// An expired value served within the opt-in stale-while-revalidate window (<see cref="CacheSettings.StaleWhileRevalidateWindow"/>) while a background refresh is triggered.
	/// </summary>
	Stale,
	/// <summary>
	/// A fresh value produced by a shared refresh (single-flight) that this call waited on.
	/// </summary>
	Fresh,
	/// <summary>
	/// A fresh value produced by this call's own value factory invocation.
	/// </summary>
	Factory
}

/// <summary>
/// A receipt describing the origin of a cache read. The receipt is diagnostic only - it never changes the value that was read.
/// </summary>
/// <param name="Source">Where the value was served from.</param>
/// <param name="SourceLayerIndex">
/// The index of the cache layer that served the value, or <c>-1</c> when the value was not served from a cache layer
/// (for example when it was produced by the value factory or a shared refresh).
/// </param>
public readonly record struct CacheFetchReceipt(CacheFetchSource Source, int SourceLayerIndex);

/// <summary>
/// The result of a cache read: the value itself and a <see cref="CacheFetchReceipt"/> describing where the value came from.
/// </summary>
/// <typeparam name="T">The type of value in the cache entry.</typeparam>
/// <param name="Value">The value that was read. This is the same value the non-receipt APIs return.</param>
/// <param name="Receipt">The source receipt for this read.</param>
public readonly record struct CacheFetchResult<T>(T? Value, CacheFetchReceipt Receipt);
