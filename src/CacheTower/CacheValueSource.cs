namespace CacheTower
{
	/// <summary>
	/// Describes where the value returned for a read came from.
	/// </summary>
	/// <remarks>
	/// Together with <see cref="CacheValueFreshness"/> this forms the source receipt for a read.
	/// For example, an expired entry served from the first cache layer under stale-while-revalidate is reported as
	/// <see cref="MemoryLayer"/> with <see cref="CacheValueFreshness.Stale"/>.
	/// </remarks>
	public enum CacheValueSource
	{
		/// <summary>
		/// The value was produced by the value factory because no usable cache entry existed.
		/// </summary>
		Factory = 0,
		/// <summary>
		/// The value was read from the first (highest priority, typically in-memory) cache layer.
		/// </summary>
		MemoryLayer = 1,
		/// <summary>
		/// The value was read from a cache layer below the first - for example a file or distributed cache layer.
		/// </summary>
		BackingLayer = 2
	}
}
