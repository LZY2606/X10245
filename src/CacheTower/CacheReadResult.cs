using System;

namespace CacheTower;

/// <summary>
/// Describes where the value of a cache read came from.
/// </summary>
/// <remarks>
/// The declaration order of these values is stable and can be relied upon for diagnostics and ordering.
/// </remarks>
public enum CacheReadSource
{
	/// <summary>
	/// The source of the value could not be determined. This is never produced for a successful read.
	/// </summary>
	Unknown = 0,
	/// <summary>
	/// An unexpired value served from the first (top-most, typically in-memory) cache layer.
	/// </summary>
	MemoryLayer,
	/// <summary>
	/// An unexpired value served from a lower (backing/fallback) cache layer.
	/// </summary>
	FallbackLayer,
	/// <summary>
	/// An expired value served within the stale-while-revalidate window while a background refresh is in flight.
	/// </summary>
	Stale,
	/// <summary>
	/// A value produced by an in-flight refresh that this call waited on (single-flight) rather than executed itself.
	/// </summary>
	Fresh,
	/// <summary>
	/// A value produced by the value factory executed by this call.
	/// </summary>
	Factory
}

/// <summary>
/// The result of a cache read that carries a <see cref="CacheReadSource"/> receipt alongside the value.
/// The receipt describes the origin of the value and never changes the value itself.
/// </summary>
/// <typeparam name="T">The type of the cached value.</typeparam>
/// <param name="Value">The value that was read.</param>
/// <param name="Source">The receipt describing where <paramref name="Value"/> came from.</param>
public readonly record struct CacheReadResult<T>(T? Value, CacheReadSource Source)
{
	/// <summary>
	/// The value that was read.
	/// </summary>
	public T? Value { get; } = Value;

	/// <summary>
	/// The receipt describing where the value came from.
	/// </summary>
	public CacheReadSource Source { get; } = Source;
}
