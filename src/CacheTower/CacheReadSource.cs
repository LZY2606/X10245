using System;

namespace CacheTower;

/// <summary>
/// Describes the source and freshness of a value returned by a cache read.
/// </summary>
[Flags]
public enum CacheReadSource
{
	/// <summary>
	/// The source could not be classified.
	/// </summary>
	Unknown = 0,
	/// <summary>
	/// The value was produced by the value factory.
	/// </summary>
	Factory = 1,
	/// <summary>
	/// The value was read from the first, in-memory cache layer.
	/// </summary>
	MemoryLayer = 2,
	/// <summary>
	/// The value was read from a cache layer below the first layer.
	/// </summary>
	BackingLayer = 4,
	/// <summary>
	/// The value was fresh.
	/// </summary>
	Fresh = 8,
	/// <summary>
	/// The value was stale.
	/// </summary>
	Stale = 16,
	/// <summary>
	/// The read scheduled a single background refresh.
	/// </summary>
	RevalidationScheduled = 32
}
