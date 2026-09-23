using System;

namespace CacheTower;

/// <summary>
/// Describes how a cache stack should treat an entry when reading it.
/// </summary>
public enum CacheReadMode
{
	/// <summary>
	/// Uses the cache stack's standard read behaviour. Expired entries force a refresh.
	/// </summary>
	Default = 0,
	/// <summary>
	/// Allows an expired entry to be returned for a limited time while it is refreshed.
	/// </summary>
	StaleWhileRevalidate = 1
}
