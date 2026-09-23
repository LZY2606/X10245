using System;

namespace CacheTower
{
	/// <summary>
	/// Describes where the value returned by <see cref="ICacheStack.GetOrSetAsync{T}(string, Func{T, Task{T}}, CacheSettings, System.Threading.CancellationToken)"/> originated from.
	/// </summary>
	public enum CacheValueOrigin
	{
		/// <summary>
		/// The value was produced by the value factory because no usable cache entry existed.
		/// </summary>
		Factory,
		/// <summary>
		/// The value was read from one of the cache stack's cache layers.
		/// </summary>
		CacheLayer
	}

	/// <summary>
	/// Describes the freshness of the value at the moment it was read.
	/// </summary>
	public enum CacheValueFreshness
	{
		/// <summary>
		/// The value was read before its stale date and required no refresh.
		/// </summary>
		Fresh,
		/// <summary>
		/// The value was past its stale date.
		/// This covers both a still-unexpired stale entry (which triggers a background refresh) and an expired entry served within the stale-while-revalidate window.
		/// </summary>
		Stale,
		/// <summary>
		/// The value was newly produced by the value factory and was not read from any cache layer.
		/// </summary>
		Factory
	}

	/// <summary>
	/// A provenance receipt for a single read from a <see cref="CacheStack"/>.
	/// </summary>
	/// <remarks>
	/// Receipts describe how a value was sourced without altering the value itself.
	/// They are attached to reads performed through the stale-while-revalidate aware overloads of <c>GetOrSetAsync</c>.
	/// </remarks>
	public readonly record struct CacheReadReceipt
	{
		/// <summary>
		/// Whether the value originated from the value factory or from a cache layer.
		/// </summary>
		public CacheValueOrigin Origin { get; }

		/// <summary>
		/// The freshness of the value when it was returned to the caller.
		/// </summary>
		public CacheValueFreshness Freshness { get; }

		/// <summary>
		/// The index of the cache layer the value was read from, or <see langword="null"/> when the value was produced by the value factory.
		/// </summary>
		/// <remarks>
		/// An index of zero represents the first (highest priority) cache layer - typically the in-memory layer.
		/// </remarks>
		public int? LayerIndex { get; }

		/// <summary>
		/// Creates a new <see cref="CacheReadReceipt"/>.
		/// </summary>
		/// <param name="origin">Where the value originated from.</param>
		/// <param name="freshness">The freshness of the returned value.</param>
		/// <param name="layerIndex">The index of the cache layer read, or <see langword="null"/> when produced by the factory.</param>
		/// <exception cref="ArgumentOutOfRangeException">
		/// Thrown when <paramref name="origin"/> is <see cref="CacheValueOrigin.Factory"/> but a layer index was provided,
		/// or when <paramref name="origin"/> is <see cref="CacheValueOrigin.CacheLayer"/> without a layer index.
		/// </exception>
		public CacheReadReceipt(CacheValueOrigin origin, CacheValueFreshness freshness, int? layerIndex)
		{
			if (origin == CacheValueOrigin.Factory && layerIndex.HasValue)
			{
				throw new ArgumentOutOfRangeException(nameof(layerIndex), "A factory sourced value cannot reference a cache layer.");
			}

			if (origin == CacheValueOrigin.CacheLayer && (!layerIndex.HasValue || layerIndex.Value < 0))
			{
				throw new ArgumentOutOfRangeException(nameof(layerIndex), "A cache layer sourced value must reference a non-negative cache layer index.");
			}

			Origin = origin;
			Freshness = freshness;
			LayerIndex = layerIndex;
		}

		/// <summary>
		/// Creates a receipt for a value produced by the value factory.
		/// </summary>
		public static CacheReadReceipt FromFactory() => new(CacheValueOrigin.Factory, CacheValueFreshness.Factory, null);

		/// <summary>
		/// Creates a receipt for a fresh value read from the cache layer at <paramref name="layerIndex"/>.
		/// </summary>
		public static CacheReadReceipt FreshFromLayer(int layerIndex) => new(CacheValueOrigin.CacheLayer, CacheValueFreshness.Fresh, layerIndex);

		/// <summary>
		/// Creates a receipt for a stale value read from the cache layer at <paramref name="layerIndex"/>.
		/// </summary>
		public static CacheReadReceipt StaleFromLayer(int layerIndex) => new(CacheValueOrigin.CacheLayer, CacheValueFreshness.Stale, layerIndex);

		/// <summary>
		/// Gets whether the receipt identifies a value read from a cache layer.
		/// </summary>
		public bool IsFromCacheLayer => Origin == CacheValueOrigin.CacheLayer;

		/// <summary>
		/// Gets whether the receipt identifies a value produced by the value factory.
		/// </summary>
		public bool IsFromFactory => Origin == CacheValueOrigin.Factory;

		/// <summary>
		/// Gets whether the returned value was stale.
		/// </summary>
		public bool IsStale => Freshness == CacheValueFreshness.Stale;

		/// <summary>
		/// Gets whether the value was read from the first (highest priority) cache layer.
		/// </summary>
		public bool IsFromPrimaryLayer => LayerIndex == 0;

		/// <inheritdoc/>
		public override string ToString()
		{
			return LayerIndex.HasValue
				? FormattableString.Invariant($"{Origin}/{Freshness}/Layer{LayerIndex.Value}")
				: $"{Origin}/{Freshness}";
		}
	}

	/// <summary>
	/// The result of a stale-while-revalidate aware read, pairing the value with its <see cref="CacheReadReceipt"/>.
	/// </summary>
	/// <typeparam name="T">The type of the cached value.</typeparam>
	/// <param name="Value">The value returned to the caller. This is never altered by the receipt.</param>
	/// <param name="Receipt">The provenance receipt describing how <paramref name="Value"/> was sourced.</param>
	public readonly record struct CacheReadResult<T>(T Value, CacheReadReceipt Receipt)
	{
		/// <summary>
		/// Supports deconstruction into the value and its receipt.
		/// </summary>
		public void Deconstruct(out T value, out CacheReadReceipt receipt)
		{
			value = Value;
			receipt = Receipt;
		}
	}
}
