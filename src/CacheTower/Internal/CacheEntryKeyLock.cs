using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace CacheTower.Internal;

/// <summary>
/// Notification released when a key's refresh completes.
/// Wraps the cache entry with refresh metadata that waiting callers need to build a correct source receipt.
/// </summary>
internal sealed class RefreshResultEntry<T> : ICacheEntry
{
	public RefreshResultEntry(CacheEntry<T> cacheEntry, bool servedFromFactory)
	{
		CacheEntry = cacheEntry;
		ServedFromFactory = servedFromFactory;
	}

	public CacheEntry<T> CacheEntry { get; }
	public bool ServedFromFactory { get; }

	public DateTime Expiry => CacheEntry.Expiry;
}

internal readonly struct CacheEntryKeyLock
{
	private readonly Dictionary<string, TaskCompletionSource<ICacheEntry>?> keyLocks = new(StringComparer.Ordinal);

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

	/// <summary>
	/// The number of keys that currently have an in-flight refresh or waiters.
	/// Intended for internal diagnostics and tests verifying single-flight state is cleaned up.
	/// </summary>
	public int ActiveRefreshCount
	{
		get
		{
			lock (keyLocks)
			{
				return keyLocks.Count;
			}
		}
	}

	public Task<ICacheEntry> WaitAsync(string cacheKey)
	{
		TaskCompletionSource<ICacheEntry>? completionSource;

		lock (keyLocks)
		{
			if (!keyLocks.TryGetValue(cacheKey, out completionSource) || completionSource == null)
			{
				completionSource = new TaskCompletionSource<ICacheEntry>();
				keyLocks[cacheKey] = completionSource;
			}
		}

		return completionSource.Task;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private bool TryRemove(string cacheKey, out TaskCompletionSource<ICacheEntry>? completionSource)
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
