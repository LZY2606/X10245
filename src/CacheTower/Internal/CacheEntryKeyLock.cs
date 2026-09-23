using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace CacheTower.Internal;

internal readonly struct CacheEntryKeyLock
{
	private sealed class LockState
	{
		public TaskCompletionSource<ICacheEntry>? CompletionSource;
		public int EvictionVersion;
	}

	private readonly Dictionary<string, LockState> keyLocks = new(StringComparer.Ordinal);

	public CacheEntryKeyLock() { }

	public bool AcquireLock(string cacheKey)
	{
		lock (keyLocks)
		{
			if (keyLocks.ContainsKey(cacheKey))
			{
				return false;
			}
			keyLocks[cacheKey] = new LockState();
			return true;
		}
	}

	public Task<ICacheEntry> WaitAsync(string cacheKey)
	{
		LockState? lockState;

		lock (keyLocks)
		{
			if (!keyLocks.TryGetValue(cacheKey, out lockState))
			{
				lockState = new LockState();
				keyLocks[cacheKey] = lockState;
			}
			if (lockState.CompletionSource == null)
			{
				lockState.CompletionSource = new TaskCompletionSource<ICacheEntry>();
			}
		}

		return lockState.CompletionSource.Task;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private bool TryRemove(string cacheKey, out LockState? lockState)
	{
		lock (keyLocks)
		{
			if (keyLocks.TryGetValue(cacheKey, out lockState))
			{
				keyLocks.Remove(cacheKey);
				return true;
			}
			return false;
		}
	}

	/// <summary>
	/// Returns the current eviction version for <paramref name="cacheKey"/>.
	/// An in-flight refresh captures this before running and compares it before writing;
	/// a mismatch means an eviction raced the refresh and the refreshed value must not be resurrected into the cache.
	/// </summary>
	public int GetEvictionVersion(string cacheKey)
	{
		lock (keyLocks)
		{
			return keyLocks.TryGetValue(cacheKey, out var lockState) ? lockState.EvictionVersion : 0;
		}
	}

	/// <summary>
	/// Records that <paramref name="cacheKey"/> was evicted. Only tracked while a refresh holds the key lock;
	/// evictions outside a refresh need no tracking.
	/// </summary>
	public void NotifyEviction(string cacheKey)
	{
		lock (keyLocks)
		{
			if (keyLocks.TryGetValue(cacheKey, out var lockState))
			{
				lockState.EvictionVersion++;
			}
		}
	}

	public void ReleaseLock(string cacheKey, ICacheEntry cacheEntry)
	{
		if (TryRemove(cacheKey, out var lockState))
		{
			lockState?.CompletionSource?.TrySetResult(cacheEntry);
		}
	}

	public void ReleaseLock(string cacheKey, Exception exception)
	{
		if (TryRemove(cacheKey, out var lockState))
		{
			lockState?.CompletionSource?.SetException(exception);
		}
	}
}
