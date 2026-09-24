namespace CacheTower;

/// <summary>
/// Describes where a read value came from without modifying it.
/// </summary>
/// <param name="Source">The classified source and freshness of the value.</param>
/// <param name="LayerIndex">The zero-based cache layer index, or <c>-1</c> when the value did not come from a layer.</param>
public readonly record struct CacheReadReceipt(CacheReadSource Source, int LayerIndex)
{
	/// <summary>
	/// Gets whether the receipt contains the supplied source flag.
	/// </summary>
	/// <param name="source">The source flag to check.</param>
	/// <returns><see langword="true"/> when the flag is present; otherwise <see langword="false"/>.</returns>
	public bool HasSource(CacheReadSource source) => (Source & source) == source;
}
