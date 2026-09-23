using System;
using System.Collections.Generic;

namespace CacheTower.Internal;

/// <summary>
/// Tracks eviction and flush versions per cache key so an in-flight refresh can detect
/// that the key it is refreshing was deleted and avoid resurrecting the old value.
/// </summary>
internal sealed class EvictionVersionTracker
{
	private readonly Dictionary<string, long> KeyVersions = new(StringComparer.Ordinal);
	private long GlobalVersion;

	/// <summary>
	/// Gets the current version for the given <paramref name="cacheKey"/>.
	/// </summary>
	public EvictionVersion GetVersion(string cacheKey)
	{
		lock (KeyVersions)
		{
			KeyVersions.TryGetValue(cacheKey, out var keyVersion);
			return new(GlobalVersion, keyVersion);
		}
	}

	/// <summary>
	/// Records that <paramref name="cacheKey"/> is being evicted. Must be called before the layers are evicted
	/// so that a refresh completing afterwards can observe the deletion.
	/// </summary>
	public void NotifyEvicted(string cacheKey)
	{
		lock (KeyVersions)
		{
			KeyVersions[cacheKey] = KeyVersions.TryGetValue(cacheKey, out var version) ? version + 1 : 1;
		}
	}

	/// <summary>
	/// Records that the whole cache stack is being flushed.
	/// </summary>
	public void NotifyFlushed()
	{
		lock (KeyVersions)
		{
			GlobalVersion++;
			KeyVersions.Clear();
		}
	}

	/// <summary>
	/// Drops the tracked version for <paramref name="cacheKey"/> if it has not changed since
	/// <paramref name="version"/> was captured. Keeps the tracker bounded to keys with
	/// evictions that no completed refresh has observed yet.
	/// </summary>
	public void ResetIfUnchanged(string cacheKey, EvictionVersion version)
	{
		lock (KeyVersions)
		{
			if (GlobalVersion == version.GlobalVersion
				&& KeyVersions.TryGetValue(cacheKey, out var keyVersion)
				&& keyVersion == version.KeyVersion)
			{
				KeyVersions.Remove(cacheKey);
			}
		}
	}
}

/// <summary>
/// A point-in-time version of deletions observed by an <see cref="EvictionVersionTracker"/>.
/// </summary>
internal readonly record struct EvictionVersion(long GlobalVersion, long KeyVersion);
