using System.Collections.Concurrent;
using System.Collections.Generic;

namespace CacheTower.Internal;

/// <summary>
/// Tracks a monotonically increasing version per cache key for every eviction that goes through
/// <see cref="ICacheStack.EvictAsync(string)"/>.
/// Refreshes capture the version before generating a new value and compare it before writing,
/// so an eviction that races a refresh cannot have its removal undone by the refresh completing afterwards.
/// </summary>
internal sealed class EvictionTracker
{
	private readonly ConcurrentDictionary<string, long> Versions = new(System.StringComparer.Ordinal);

	/// <summary>
	/// The current eviction version for the given <paramref name="cacheKey"/>.
	/// </summary>
	public long GetVersion(string cacheKey)
	{
		return Versions.TryGetValue(cacheKey, out var version) ? version : 0;
	}

	/// <summary>
	/// Records that the given <paramref name="cacheKey"/> was evicted.
	/// </summary>
	public void MarkEvicted(string cacheKey)
	{
		Versions.AddOrUpdate(cacheKey, 1, static (_, version) => version + 1);
	}

	/// <summary>
	/// Best-effort cleanup of the tracked version once a refresh has observed it and successfully
	/// written its value, keeping the tracked state bounded. Only resets when the version is unchanged.
	/// </summary>
	public void TryReset(string cacheKey, long expectedVersion)
	{
		if (expectedVersion == 0)
		{
			return;
		}

		((ICollection<KeyValuePair<string, long>>)Versions).Remove(new(cacheKey, expectedVersion));
	}
}
