using System;

namespace CacheTower
{
	/// <summary>
	/// Describes which cache layer a value returned by a read was found in.
	/// </summary>
	public enum CacheReadLayer
	{
		/// <summary>
		/// No cache layer provided the value - it was produced by the value factory.
		/// </summary>
		None = 0,
		/// <summary>
		/// The value came from the first (highest priority, typically memory) cache layer.
		/// </summary>
		MemoryLayer = 1,
		/// <summary>
		/// The value came from a lower priority (backing) cache layer.
		/// </summary>
		BackingLayer = 2
	}

	/// <summary>
	/// Describes the freshness state of a value returned by a read.
	/// </summary>
	public enum CacheReadState
	{
		/// <summary>
		/// The value was found in a cache layer before its expiry.
		/// </summary>
		Fresh = 0,
		/// <summary>
		/// The value had expired but was returned within the stale-while-revalidate window while a refresh was triggered.
		/// </summary>
		Stale = 1,
		/// <summary>
		/// The value was produced by the value factory during the read (cache miss or an entry expired beyond the window).
		/// </summary>
		Factory = 2
	}

	/// <summary>
	/// A provenance receipt for a single read through the cache stack.
	/// </summary>
	/// <remarks>
	/// The receipt is deliberately separate from the cached value and never changes it.
	/// <see cref="Layer"/> identifies the layer that provided the value (memory layer, backing layer or none for factory values);
	/// <see cref="State"/> identifies its freshness (fresh, stale-while-revalidate or factory produced).
	/// </remarks>
	public readonly record struct CacheReadReceipt(CacheReadLayer Layer, CacheReadState State)
	{
		/// <summary>
		/// The index of the cache layer the value came from, or <c>-1</c> when no layer provided it.
		/// </summary>
		public int LayerIndex { get; init; } = -1;

		/// <summary>
		/// Returns a receipt for a value found in a cache layer.
		/// </summary>
		public static CacheReadReceipt FromLayer(int layerIndex, CacheReadState state)
		{
			if (layerIndex < 0)
			{
				throw new ArgumentOutOfRangeException(nameof(layerIndex));
			}

			return new(layerIndex == 0 ? CacheReadLayer.MemoryLayer : CacheReadLayer.BackingLayer, state)
			{
				LayerIndex = layerIndex
			};
		}

		/// <summary>
		/// Returns a receipt for a value produced by the value factory. When the layer of the produced
		/// value cannot be determined, this is reported explicitly rather than guessed.
		/// </summary>
		public static CacheReadReceipt FromFactory() => new(CacheReadLayer.None, CacheReadState.Factory);

		/// <summary>
		/// Returns <see langword="true"/> when the receipt reports a value that was still fresh.
		/// </summary>
		public bool IsFresh => State == CacheReadState.Fresh;
		/// <summary>
		/// Returns <see langword="true"/> when the receipt reports a stale value served within the stale-while-revalidate window.
		/// </summary>
		public bool IsStale => State == CacheReadState.Stale;
		/// <summary>
		/// Returns <see langword="true"/> when the receipt reports a value produced by the value factory.
		/// </summary>
		public bool IsFactory => State == CacheReadState.Factory;
	}

	/// <summary>
	/// The outcome of a single read: the value and the provenance receipt that describes where it came from.
	/// </summary>
	/// <typeparam name="T">The type of value in the cache entry.</typeparam>
	public readonly record struct CacheReadResult<T>(T? Value, CacheReadReceipt Receipt);
}
