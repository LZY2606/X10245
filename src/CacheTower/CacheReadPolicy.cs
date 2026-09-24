namespace CacheTower;

/// <summary>
/// Describes the read policy options for a <see cref="CacheStack"/>.
/// </summary>
namespace CacheTower;

/// <summary>
/// Describes how an expired cache entry is treated by a read.
/// </summary>
public enum CacheReadPolicy
{
	/// <summary>
	/// Expired entries are not returned and force a value factory refresh.
	/// </summary>
	Default = 0,
	/// <summary>
	/// Expired entries may be returned for a limited time while a single refresh is performed.
	/// </summary>
	StaleWhileRevalidate = 1
}
