using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace CacheTower.Internal;

internal readonly struct CacheEntryKeyLock
{
	private sealed class LockEntry
	{
		public TaskCompletionSource<ICacheEntry>? CompletionSource;
		public long EvictionVersion;
	}

	private readonly Dictionary<string, LockEntry> keyLocks = new(StringComparer.Ordinal);
	private readonly Dictionary<string, long> evictionMarkers = new(StringComparer.Ordinal);

	public CacheEntryKeyLock() { }

	/// <summary>
	/// Attempts to acquire the single-flight refresh lock for <paramref name="cacheKey"/>.
	/// </summary>
	/// <returns><see langword="true"/> when the lock was acquired; <see langword="false"/> when a refresh is already in flight.</returns>
	public bool AcquireLock(string cacheKey)
	{
		lock (keyLocks)
		{
#if NETSTANDARD2_0
			if (keyLocks.ContainsKey(cacheKey))
			{
				return false;
			}

			keyLocks[cacheKey] = new LockEntry();
			return true;
#elif NETSTANDARD2_1
			return keyLocks.TryAdd(cacheKey, new LockEntry());
#endif
		}
	}

	/// <summary>
	/// Waits until the in-flight refresh for <paramref name="cacheKey"/> completes.
	/// </summary>
	/// <remarks>
	/// The resulting task is cancelled when an eviction happens while waiting so the caller re-evaluates the cache
	/// rather than receiving a value that the evict/refresh race is about to remove.
	/// </remarks>
	public Task<ICacheEntry> WaitAsync(string cacheKey)
	{
		lock (keyLocks)
		{
			if (!keyLocks.TryGetValue(cacheKey, out var entry))
			{
				entry = new LockEntry();
				keyLocks[cacheKey] = entry;
			}

			if (entry.CompletionSource == null)
			{
				entry.CompletionSource = new TaskCompletionSource<ICacheEntry>();
			}

			return entry.CompletionSource.Task;
		}
	}

	/// <summary>
	/// Marks that an eviction for <paramref name="cacheKey"/> happened and returns the new eviction version.
	/// </summary>
	/// <remarks>
	/// A refresh captures the version when it starts; if the version moves on while it is in flight, its result must
	/// not be written back - the evict/refresh completion race cannot resurrect the evicted value.
	/// </remarks>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public long MarkEviction(string cacheKey)
	{
		lock (keyLocks)
		{
			long version;
			if (evictionMarkers.TryGetValue(cacheKey, out version))
			{
				version++;
			}
			else
			{
				version = 1;
			}

			evictionMarkers[cacheKey] = version;

			if (keyLocks.TryGetValue(cacheKey, out var entry))
			{
				entry.CompletionSource?.TrySetCanceled();
			}

			return version;
		}
	}

	/// <summary>
	/// Gets the current eviction version for <paramref name="cacheKey"/>.
	/// </summary>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public long GetEvictionVersion(string cacheKey)
	{
		lock (keyLocks)
		{
			return evictionMarkers.TryGetValue(cacheKey, out var version) ? version : 0;
		}
	}

	/// <summary>
	/// Clears the eviction marker for <paramref name="cacheKey"/> when it still matches <paramref name="observedVersion"/>.
	/// </summary>
	/// <remarks>
	/// Called by a refresh after successfully publishing a value - if another eviction happened in the meantime,
	/// the marker is kept.
	/// </remarks>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public bool TryClearEvictionMarker(string cacheKey, long observedVersion)
	{
		lock (keyLocks)
		{
			if (!evictionMarkers.TryGetValue(cacheKey, out var version) || version != observedVersion)
			{
				return false;
			}

			evictionMarkers.Remove(cacheKey);
			return true;
		}
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private bool TryRemove(string cacheKey, out TaskCompletionSource<ICacheEntry>? completionSource)
	{
		lock (keyLocks)
		{
#if NETSTANDARD2_0
			if (keyLocks.TryGetValue(cacheKey, out var entry))
			{
				completionSource = entry.CompletionSource;
				keyLocks.Remove(cacheKey);
				return true;
			}

			completionSource = null;
			return false;
#elif NETSTANDARD2_1
			if (keyLocks.TryGetValue(cacheKey, out var entry))
			{
				completionSource = entry.CompletionSource;
				keyLocks.Remove(cacheKey);
				return true;
			}

			completionSource = null;
			return false;
#endif
		}
	}

	public void ReleaseLock(string cacheKey, ICacheEntry cacheEntry)
	{
		if (TryRemove(cacheKey, out var completionSource))
		{
			completionSource?.TrySetResult(cacheEntry);
		}
	}

	public void ReleaseLock(string cacheKey, Exception exception)
	{
		if (TryRemove(cacheKey, out var completionSource))
		{
			completionSource?.SetException(exception);
		}
	}
}
