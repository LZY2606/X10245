using System;

namespace CacheTower
{
	/// <summary>
	/// The result of a receipt-aware cache read - the value produced along with a receipt describing where it came from.
	/// </summary>
	/// <remarks>
	/// The receipt is diagnostic only: it describes the read that produced <see cref="Value"/> and never changes or copies that value.
	/// <para>
	/// Use <see cref="Source"/> and <see cref="Freshness"/> together to distinguish the memory layer, a backing layer, stale and fresh entries and the value factory.
	/// </para>
	/// </remarks>
	/// <typeparam name="T">The type of the cached value.</typeparam>
	public sealed record CacheReadResult<T>
	{
		/// <summary>
		/// Creates a new <see cref="CacheReadResult{T}"/>.
		/// </summary>
		/// <param name="value">The value produced for the read.</param>
		/// <param name="source">Where the value came from.</param>
		/// <param name="freshness">Whether the value is fresh or stale.</param>
		/// <param name="layerIndex">The zero-based index of the cache layer the value was read from, or <c>null</c> when <paramref name="source"/> is <see cref="CacheValueSource.Factory"/>.</param>
		/// <exception cref="ArgumentOutOfRangeException"></exception>
		public CacheReadResult(T? value, CacheValueSource source, CacheValueFreshness freshness, int? layerIndex)
		{
			if (source == CacheValueSource.Factory && layerIndex is not null)
			{
				throw new ArgumentOutOfRangeException(nameof(layerIndex), layerIndex, "A factory result cannot specify a cache layer index.");
			}

			if (source != CacheValueSource.Factory && layerIndex is null)
			{
				throw new ArgumentOutOfRangeException(nameof(layerIndex), layerIndex, "A cache layer result must specify a cache layer index.");
			}

			if (layerIndex is < 0)
			{
				throw new ArgumentOutOfRangeException(nameof(layerIndex), layerIndex, "The cache layer index cannot be negative.");
			}

			Value = value;
			Source = source;
			Freshness = freshness;
			LayerIndex = layerIndex;
		}

		/// <summary>
		/// The value produced for the read. This is the same reference that the equivalent plain <c>GetOrSetAsync</c> call would return.
		/// </summary>
		public T? Value { get; }
		/// <summary>
		/// Where the value came from - the value factory, the memory layer or a backing cache layer.
		/// </summary>
		public CacheValueSource Source { get; }
		/// <summary>
		/// Whether the value is fresh or was served stale under stale-while-revalidate.
		/// </summary>
		public CacheValueFreshness Freshness { get; }
		/// <summary>
		/// The zero-based index of the cache layer the value was read from, or <c>null</c> when the value came from the value factory.
		/// </summary>
		public int? LayerIndex { get; }

		/// <summary>
		/// Whether <see cref="Source"/> reports a backing cache layer (any layer other than the first).
		/// </summary>
		public bool IsFromBackingLayer => Source == CacheValueSource.BackingLayer;
	}
}
