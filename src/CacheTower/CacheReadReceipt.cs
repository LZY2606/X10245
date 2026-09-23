using System;

namespace CacheTower;

/// <summary>
/// Describes where the value returned from a read came from.
/// </summary>
public enum CacheReadOrigin
{
	/// <summary>
	/// No cache layer held a usable value; the value was produced by the value factory.
	/// </summary>
	Factory,
	/// <summary>
	/// The value was read from the first (highest priority, usually in-memory) cache layer.
	/// </summary>
	MemoryLayer,
	/// <summary>
	/// The value was read from a fallback cache layer other than the first (for example a file or distributed layer).
	/// </summary>
	FallbackLayer,
}

/// <summary>
/// Describes whether the value returned from a read was fresh or stale.
/// </summary>
/// <remarks>
/// Stale values are only returned when the stale-while-revalidate read policy is opted in to
/// via <see cref="CacheReadOptions"/>.
/// </remarks>
public enum CacheReadFreshness
{
	/// <summary>
	/// The value had not passed its expiry when it was read.
	/// </summary>
	Fresh,
	/// <summary>
	/// The value had passed its expiry but was still within the configured stale-while-revalidate window.
	/// A single background refresh is triggered for the key.
	/// </summary>
	Stale,
}

/// <summary>
/// A receipt describing the source of the value returned by a cache read.
/// </summary>
/// <remarks>
/// The receipt is diagnostic only: it never affects the value returned and is safe to ignore.
/// It distinguishes the memory cache layer, fallback cache layers, stale and fresh values, and the value factory.
/// </remarks>
/// <param name="Origin">Whether the value came from the value factory, the memory cache layer or a fallback cache layer.</param>
/// <param name="Freshness">Whether the value was fresh or stale at the time it was read.</param>
/// <param name="LayerIndex">
/// The index of the cache layer the value was read from, or <c>-1</c> when <see cref="Origin"/> is <see cref="CacheReadOrigin.Factory"/>.
/// Layer zero is the memory (first) layer; a non-zero index indicates a fallback layer.
/// </param>
public sealed record CacheReadReceipt(CacheReadOrigin Origin, CacheReadFreshness Freshness, int LayerIndex)
{
	/// <summary>
	/// Creates a receipt for a value produced by the value factory.
	/// </summary>
	public static CacheReadReceipt FromFactory() => new(CacheReadOrigin.Factory, CacheReadFreshness.Fresh, -1);

	/// <summary>
	/// Creates a receipt for a value read from a cache layer.
	/// </summary>
	/// <param name="layerIndex">The index of the cache layer the value was read from.</param>
	/// <param name="freshness">Whether the value was fresh or stale at the time it was read.</param>
	public static CacheReadReceipt FromLayer(int layerIndex, CacheReadFreshness freshness)
	{
		if (layerIndex < 0)
		{
			throw new ArgumentOutOfRangeException(nameof(layerIndex), layerIndex, "The cache layer index must not be negative.");
		}

		var origin = layerIndex == 0 ? CacheReadOrigin.MemoryLayer : CacheReadOrigin.FallbackLayer;
		return new CacheReadReceipt(origin, freshness, layerIndex);
	}

	/// <inheritdoc/>
	public override string ToString() => Origin == CacheReadOrigin.Factory
		? "Factory/Fresh"
		: $"{(Origin == CacheReadOrigin.MemoryLayer ? "MemoryLayer" : "FallbackLayer")}/{Freshness}/#{LayerIndex}";
}

/// <summary>
/// The result of a cache read performed with a source receipt.
/// </summary>
/// <typeparam name="T">The type of value returned by the read.</typeparam>
/// <param name="Value">The value that was read or produced. This is the exact same value that <c>GetOrSetAsync</c> would return.</param>
/// <param name="Receipt">A receipt describing where <see cref="Value"/> came from. The receipt never changes <see cref="Value"/>.</param>
public sealed record CacheReadResult<T>(T? Value, CacheReadReceipt Receipt)
{
	/// <summary>
	/// Converts the read result into its underlying <see cref="Value"/>, allowing the result to be used directly in place of the value.
	/// </summary>
	/// <param name="result">The read result to convert.</param>
	public static implicit operator T?(CacheReadResult<T>? result) => result is null ? default : result.Value;
}
