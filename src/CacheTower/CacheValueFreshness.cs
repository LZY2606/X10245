namespace CacheTower
{
	/// <summary>
	/// Describes the freshness of the value returned for a read.
	/// </summary>
	/// <remarks>
	/// This is the second half of the source receipt and is reported on <see cref="CacheReadResult{T}"/> alongside <see cref="CacheValueSource"/>.
	/// </remarks>
	public enum CacheValueFreshness
	{
		/// <summary>
		/// The value is fresh - it came from an unexpired cache entry or from the value factory.
		/// </summary>
		Fresh = 0,
		/// <summary>
		/// The value is stale - it came from an expired entry served within the configured stale-while-revalidate window.
		/// </summary>
		Stale = 1
	}
}
