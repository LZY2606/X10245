using System;

namespace CacheTower.Internal;

/// <summary>
/// A sentinel cache entry used to release a key lock after an eviction.
/// Waiters that receive this sentinel know the key was evicted while they were waiting
/// and must re-evaluate the cache rather than consume a value.
/// </summary>
internal sealed class EvictedCacheEntry : ICacheEntry
{
	internal static readonly EvictedCacheEntry Instance = new();

	private EvictedCacheEntry() { }

	/// <inheritdoc/>
	public DateTime Expiry => DateTime.MinValue;
}
