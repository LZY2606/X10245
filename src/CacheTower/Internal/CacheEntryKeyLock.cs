using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace CacheTower.Internal;

/// <summary>
/// The outcome handed to callers waiting on a cache key lock: the resolved cache entry and the source it was resolved from.
/// </summary>
internal readonly struct CacheEntryRefreshResult
{
	public CacheEntryRefreshResult(ICacheEntry cacheEntry, CacheValueSource source)
	{
		CacheEntry = cacheEntry;
		Source = source;
	}

	/// <summary>
	/// The cache entry that was resolved while the lock was held.
	/// </summary>
	public ICacheEntry CacheEntry { get; }
	/// <summary>
	/// The source the cache entry was resolved from.
	/// </summary>
	public CacheValueSource Source { get; }
}

internal readonly struct CacheEntryKeyLock
{
	private readonly Dictionary<string, TaskCompletionSource<CacheEntryRefreshResult>?> keyLocks = new(StringComparer.Ordinal);

	public CacheEntryKeyLock() { }

	public bool AcquireLock(string cacheKey)
	{
		lock (keyLocks)
		{
#if NETSTANDARD2_0
			var hasLock = !keyLocks.ContainsKey(cacheKey);
			if (hasLock)
			{
				keyLocks[cacheKey] = null;
			}
			return hasLock;
#elif NETSTANDARD2_1
			return keyLocks.TryAdd(cacheKey, null);
#endif
		}
	}

	public Task<CacheEntryRefreshResult> WaitAsync(string cacheKey)
	{
		TaskCompletionSource<CacheEntryRefreshResult>? completionSource;

		lock (keyLocks)
		{
			if (!keyLocks.TryGetValue(cacheKey, out completionSource) || completionSource == null)
			{
				completionSource = new TaskCompletionSource<CacheEntryRefreshResult>();
				keyLocks[cacheKey] = completionSource;
			}
		}

		return completionSource.Task;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private bool TryRemove(string cacheKey, out TaskCompletionSource<CacheEntryRefreshResult>? completionSource)
	{
		lock (keyLocks)
		{
#if NETSTANDARD2_0
			if (keyLocks.TryGetValue(cacheKey, out completionSource))
			{
				keyLocks.Remove(cacheKey);
				return true;
			}
			return false;
#elif NETSTANDARD2_1
			return keyLocks.Remove(cacheKey, out completionSource);
#endif
		}
	}

	public void ReleaseLock(string cacheKey, ICacheEntry cacheEntry, CacheValueSource source)
	{
		if (TryRemove(cacheKey, out var completionSource))
		{
			completionSource?.TrySetResult(new CacheEntryRefreshResult(cacheEntry, source));
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
