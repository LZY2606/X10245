using System;

namespace CacheTower;

/// <summary>
/// Describes where the value of a cache read actually came from.
/// </summary>
/// <remarks>
/// The source is a provenance receipt only; it never changes the value that was returned.
/// </remarks>
public enum CacheFetchSource
{
	/// <summary>
	/// The value was a fresh (unexpired) hit on the first, top-most cache layer.
	/// </summary>
	MemoryLayer,
	/// <summary>
	/// The value was a fresh (unexpired) hit on a lower-priority backing cache layer.
	/// </summary>
	BackingLayer,
	/// <summary>
	/// The value was expired but still inside the opt-in stale-while-revalidate window
	/// (<see cref="CacheSettings.StaleWhileRevalidateWindow"/>). A background refresh is triggered for the cache key.
	/// </summary>
	Stale,
	/// <summary>
	/// The value was produced by an in-flight shared refresh owned by another caller (single-flight).
	/// </summary>
	Fresh,
	/// <summary>
	/// The value was produced by the value factory of this call.
	/// </summary>
	Factory
}

/// <summary>
/// The result of a cache read together with its provenance receipt.
/// </summary>
/// <typeparam name="T">The type of value fetched.</typeparam>
/// <param name="Value">The value that was fetched. The receipt never alters this value.</param>
/// <param name="Source">Where <paramref name="Value"/> came from.</param>
public sealed record CacheFetchResult<T>(T? Value, CacheFetchSource Source);
