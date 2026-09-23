using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace CacheTower.Internal;

/// <summary>
/// The notification passed to waiters of a key lock when the lock is released with a cache entry.
/// </summary>
internal readonly struct CacheKeyLockNotification
{
	/// <summary>
	/// The cache entry the lock was released with.
	/// </summary>
	public ICacheEntry CacheEntry { get; }
	/// <summary>
	/// Where the cache entry was sourced from.
	/// </summary>
	public CacheReadSource Source { get; }

	public CacheKeyLockNotification(ICacheEntry cacheEntry, CacheReadSource source)
	{
		CacheEntry = cacheEntry;
		Source = source;
	}
}

internal readonly struct CacheEntryKeyLock
{
	private readonly Dictionary<string, TaskCompletionSource<CacheKeyLockNotification>?> keyLocks = new(StringComparer.Ordinal);
	private readonly HashSet<string> evictedKeys = new(StringComparer.Ordinal);

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

	public Task<CacheKeyLockNotification> WaitAsync(string cacheKey)
	{
		TaskCompletionSource<CacheKeyLockNotification>? completionSource;

		lock (keyLocks)
		{
			if (!keyLocks.TryGetValue(cacheKey, out completionSource) || completionSource == null)
			{
				completionSource = new TaskCompletionSource<CacheKeyLockNotification>();
				keyLocks[cacheKey] = completionSource;
			}
		}

		return completionSource.Task;
	}

	/// <summary>
	/// Returns a task that completes when the current lock holder for <paramref name="cacheKey"/> releases the lock, or <see langword="null"/> when no refresh is in flight for the key.
	/// </summary>
	public Task<CacheKeyLockNotification>? WaitForCompletionAsync(string cacheKey)
	{
		lock (keyLocks)
		{
			if (!keyLocks.ContainsKey(cacheKey))
			{
				return null;
			}
		}
		return WaitAsync(cacheKey);
	}

	/// <summary>
	/// Marks <paramref name="cacheKey"/> as evicted while a refresh is in flight, so the refresh does not resurrect the evicted value when it completes.
	/// </summary>
	public void NotifyEviction(string cacheKey)
	{
		lock (keyLocks)
		{
			if (keyLocks.ContainsKey(cacheKey))
			{
				evictedKeys.Add(cacheKey);
			}
		}
	}

	/// <summary>
	/// Checks (and clears) whether <paramref name="cacheKey"/> was evicted during the current refresh. Only meaningful for the current lock holder.
	/// </summary>
	public bool ConsumeEviction(string cacheKey)
	{
		lock (keyLocks)
		{
			return evictedKeys.Remove(cacheKey);
		}
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private bool TryRemove(string cacheKey, out TaskCompletionSource<CacheKeyLockNotification>? completionSource)
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

	public void ReleaseLock(string cacheKey, ICacheEntry cacheEntry, CacheReadSource source)
	{
		if (TryRemove(cacheKey, out var completionSource))
		{
			lock (keyLocks)
			{
				evictedKeys.Remove(cacheKey);
			}
			completionSource?.TrySetResult(new CacheKeyLockNotification(cacheEntry, source));
		}
	}

	public void ReleaseLock(string cacheKey, Exception exception)
	{
		if (TryRemove(cacheKey, out var completionSource))
		{
			lock (keyLocks)
			{
				evictedKeys.Remove(cacheKey);
			}
			completionSource?.SetException(exception);
		}
	}
}
