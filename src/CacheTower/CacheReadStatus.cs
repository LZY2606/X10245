namespace CacheTower;

/// <summary>
/// Describes the freshness of the value returned to the caller.
/// </summary>
public enum CacheReadStatus
{
	/// <summary>
	/// The value was within its normal freshness window.
	/// </summary>
	Fresh = 0,
	/// <summary>
	/// The value was stale, including a value returned by stale-while-revalidate.
	/// </summary>
	Stale = 1
}
