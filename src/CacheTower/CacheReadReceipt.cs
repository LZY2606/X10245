using System;

namespace CacheTower
{
	/// <summary>
	/// Describes where the value returned for a read came from, without changing the value itself.
	/// </summary>
	/// <remarks>
	/// A read served from an in-memory or backing cache layer additionally reports whether that layer value was fresh or stale,
	/// allowing the five possible origins to be distinguished:
	/// memory layer (fresh/stale), backing layer (fresh/stale) and the value factory.
	/// </remarks>
	public enum CacheReadOrigin
	{
		/// <summary>
		/// The value was produced by the value factory because no servable cache entry existed.
		/// Concurrent callers sharing the single in-flight refresh report this origin too.
		/// </summary>
		Factory = 0,
		/// <summary>
		/// The value was served from the first (highest priority) cache layer.
		/// </summary>
		MemoryLayer = 1,
		/// <summary>
		/// The value was served from a backing cache layer (any layer other than the first).
		/// </summary>
		BackingLayer = 2
	}

	/// <summary>
	/// A provenance receipt for a single read through the cache stack.
	/// </summary>
	/// <remarks>
	/// The receipt is diagnostic metadata only - it is produced alongside, and never mutates, the cached value.
	/// Combine <see cref="Origin"/> with <see cref="IsStale"/> and <see cref="LayerIndex"/> to distinguish every possible source:
	/// <list type="bullet">
	/// <item><see cref="CacheReadOrigin.MemoryLayer"/>/<see cref="CacheReadOrigin.BackingLayer"/> with <see cref="IsStale"/> <see langword="false"/> is a fresh hit.</item>
	/// <item><see cref="CacheReadOrigin.MemoryLayer"/>/<see cref="CacheReadOrigin.BackingLayer"/> with <see cref="IsStale"/> <see langword="true"/> is a stale (stale-while-revalidate) hit.</item>
	/// <item><see cref="CacheReadOrigin.Factory"/> means the value factory supplied the result.</item>
	/// </list>
	/// </remarks>
	/// <param name="Origin">The source the returned value was obtained from.</param>
	/// <param name="LayerIndex">The zero-based index of the layer the value came from, or <c>-1</c> for <see cref="CacheReadOrigin.Factory"/>.</param>
	/// <param name="IsStale">Whether the layer value was stale (expired but within the stale-while-revalidate window). Always <see langword="false"/> for <see cref="CacheReadOrigin.Factory"/>.</param>
	public sealed record CacheReadReceipt(CacheReadOrigin Origin, int LayerIndex, bool IsStale)
	{
		/// <summary>
		/// The cache key the receipt was issued for.
		/// </summary>
		public string CacheKey { get; internal init; } = string.Empty;

		/// <summary>
		/// The expiry date of the entry that served the read, or <see langword="null"/> when the value came from the factory.
		/// </summary>
		public DateTime? Expiry { get; internal init; }

		internal static CacheReadReceipt ForLayer(int layerIndex, bool isStale, string cacheKey, DateTime expiry)
			=> new(layerIndex == 0 ? CacheReadOrigin.MemoryLayer : CacheReadOrigin.BackingLayer, layerIndex, isStale)
			{
				CacheKey = cacheKey,
				Expiry = expiry
			};

		internal static CacheReadReceipt ForFactory(string cacheKey)
			=> new(CacheReadOrigin.Factory, -1, false)
			{
				CacheKey = cacheKey
			};
	}

	/// <summary>
	/// The result of a read through the cache stack: the value and the <see cref="CacheReadReceipt"/> describing where it came from.
	/// </summary>
	/// <typeparam name="T">The type of the cached value.</typeparam>
	/// <param name="Value">The value that was read or produced. This is the same instance that the non-receipt read method would return.</param>
	/// <param name="Receipt">The provenance receipt for this read. Never <see langword="null"/>.</param>
	public sealed record CacheReadResult<T>(T Value, CacheReadReceipt Receipt)
	{
		/// <summary>
		/// Implicitly unwraps the <see cref="Value"/>, allowing a <see cref="CacheReadResult{T}"/> to be used where a <typeparamref name="T"/> is expected.
		/// </summary>
		/// <param name="result">The read result to unwrap.</param>
		public static implicit operator T(CacheReadResult<T> result) => result.Value;
	}
}
