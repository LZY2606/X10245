using System;

namespace CacheTower;

/// <summary>
/// Describes where a read value came from without modifying the returned value.
/// </summary>
public readonly record struct CacheReadReceipt
{
	/// <summary>
	/// The source that produced the value.
	/// </summary>
	public CacheReadSource Source { get; }

/// <summary>
/// Whether the returned value was fresh or stale.
/// </summary>
	public CacheReadStatus Status { get; }

	/// <summary>
	/// The zero-based index of the cache layer that supplied the value, or <c>-1</c> for a factory value.
	/// </summary>
	public int LayerIndex { get; }

	/// <summary>
	/// The type of cache layer that supplied the value, or <c>null</c> for a factory value.
	/// </summary>
	public Type? LayerType { get; }

	private CacheReadReceipt(CacheReadSource source, CacheReadStatus status, int layerIndex, Type? layerType)
	{
		Source = source;
		Status = status;
		LayerIndex = layerIndex;
		LayerType = layerType;
	}

	/// <summary>
	/// Creates a receipt for a value read from a cache layer.
	/// </summary>
	/// <param name="layerIndex">The zero-based index of the cache layer.</param>
	/// <param name="layerType">The cache layer type.</param>
	/// <param name="status">The freshness status of the value.</param>
	/// <returns>A cache layer receipt.</returns>
	public static CacheReadReceipt FromCacheLayer(int layerIndex, Type layerType, CacheReadStatus status)
	{
		if (layerIndex < 0)
		{
			throw new ArgumentOutOfRangeException(nameof(layerIndex));
		}

		if (layerType == null)
		{
			throw new ArgumentNullException(nameof(layerType));
		}

		if (!Enum.IsDefined(typeof(CacheReadStatus), status))
		{
			throw new ArgumentOutOfRangeException(nameof(status), status, "Unsupported cache read status");
		}

		return new(CacheReadSource.CacheLayer, status, layerIndex, layerType);
	}

	/// <summary>
	/// Creates a receipt for a value produced by the factory.
	/// </summary>
	/// <returns>A value factory receipt.</returns>
	public static CacheReadReceipt FromValueFactory() =>
		new(CacheReadSource.ValueFactory, CacheReadStatus.Fresh, -1, null);

	/// <inheritdoc/>
	public override string ToString()
	{
		if (Source == CacheReadSource.ValueFactory)
		{
			return "CacheReadReceipt { Source=ValueFactory, Status=Fresh }";
		}

		return $"CacheReadReceipt {{ Source=CacheLayer, Status={Status}, LayerIndex={LayerIndex}, LayerType={LayerType!.Name} }}";
	}
}
