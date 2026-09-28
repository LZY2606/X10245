using System;
using CacheTower.Internal;

namespace CacheTower
{
	/// <summary>
	/// Describes where the value returned from
	/// <see cref="ICacheStack.GetOrSetAsync{T}(string, Func{T, Task{T}}, CacheSettings, CacheReadOptions, System.Threading.CancellationToken)"/>
	/// originated from.
	/// </summary>
	public enum CacheReadOrigin
	{
		/// <summary>
		/// The origin of the value could not be determined.
		/// <para>
		/// A receipt with this origin is never produced by a successful cache read.
		/// It is reserved as an explicit "unknown" state so callers can detect unsupported
		/// or unrecognised conditions instead of assuming success.
		/// </para>
		/// </summary>
		Unknown = 0,
		/// <summary>
		/// The value was served from the first (highest priority) cache layer, for example an in-memory layer.
		/// </summary>
		MemoryLayer = 1,
		/// <summary>
		/// The value was served from a cache layer other than the first, for example a file or distributed layer.
		/// </summary>
		BackupLayer = 2,
		/// <summary>
		/// The value was produced by the value factory because no servable cache entry existed.
		/// </summary>
		Factory = 3
	}

	/// <summary>
	/// Describes whether the value returned from a cache read was fresh or stale.
	/// </summary>
	public enum CacheValueFreshness
	{
		/// <summary>
		/// The freshness of the value could not be determined.
		/// <para>
		/// A receipt with this freshness is never produced by a successful cache read.
		/// It is reserved as an explicit "unknown" state so callers can detect unsupported
		/// or unrecognised conditions instead of assuming success.
		/// </para>
		/// </summary>
		Unknown = 0,
		/// <summary>
		/// The value was within its configured time-to-live at the time of the read.
		/// </summary>
		Fresh = 1,
		/// <summary>
		/// The value was past its expiry but was still within the opt-in stale-while-revalidate window.
		/// </summary>
		Stale = 2
	}

	/// <summary>
	/// A receipt describing the provenance of a value returned from a cache read without altering the value itself.
	/// </summary>
	/// <remarks>
	/// The receipt is diagnostic metadata - it is returned alongside the value and never changes how the value is cached, refreshed or compared.
	/// </remarks>
	public readonly record struct CacheSourceReceipt
	{
		/// <summary>
		/// The cache layer or factory the value originated from.
		/// </summary>
		public CacheReadOrigin Origin { get; }
		/// <summary>
		/// Whether the returned value was fresh or stale at the time of the read.
		/// </summary>
		public CacheValueFreshness Freshness { get; }
		/// <summary>
		/// The zero-based index of the cache layer the value was served from, or <c>-1</c> when the value came from the factory.
		/// </summary>
		public int LayerIndex { get; }
		/// <summary>
		/// The time the read decision was made, using the same clock the cache stack uses to evaluate entry expiry.
		/// </summary>
		public DateTime ReadTime { get; }

		/// <summary>
		/// Creates a new <see cref="CacheSourceReceipt"/>.
		/// </summary>
		/// <param name="origin">The cache layer or factory the value originated from.</param>
		/// <param name="freshness">Whether the returned value was fresh or stale at the time of the read.</param>
		/// <param name="layerIndex">The zero-based index of the serving cache layer, or <c>-1</c> when the value came from the factory.</param>
		/// <param name="readTime">The time the read decision was made.</param>
		public CacheSourceReceipt(CacheReadOrigin origin, CacheValueFreshness freshness, int layerIndex, DateTime readTime)
		{
			Origin = origin;
			Freshness = freshness;
			LayerIndex = layerIndex;
			ReadTime = readTime;
		}

		/// <summary>
		/// Returns <see langword="true"/> when the value was served from any cache layer rather than the factory.
		/// </summary>
		public bool IsFromCacheLayer => Origin == CacheReadOrigin.MemoryLayer || Origin == CacheReadOrigin.BackupLayer;
		/// <summary>
		/// Returns <see langword="true"/> when the value was served stale (expired but within the stale-while-revalidate window).
		/// </summary>
		public bool IsStale => Freshness == CacheValueFreshness.Stale;

		internal static CacheSourceReceipt ForFactory(DateTime readTime)
			=> new(CacheReadOrigin.Factory, CacheValueFreshness.Fresh, -1, readTime);

		internal static CacheSourceReceipt ForLayer(int layerIndex, CacheValueFreshness freshness, DateTime readTime)
		{
			if (layerIndex < 0)
			{
				throw new ArgumentOutOfRangeException(nameof(layerIndex), layerIndex, "The cache layer index must be non-negative.");
			}

			var origin = layerIndex == 0 ? CacheReadOrigin.MemoryLayer : CacheReadOrigin.BackupLayer;
			return new(origin, freshness, layerIndex, readTime);
		}
	}
}
