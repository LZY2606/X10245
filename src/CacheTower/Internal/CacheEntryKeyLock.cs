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
		public bool Evicted;
	}

	private readonly Dictionary<string, LockState> keyLocks = new(StringComparer.Ordinal);

	public CacheEntryKeyLock() { }

	public bool AcquireLock(string cacheKey)
	{
		lock (keyLocks)
		{
#if NETSTANDARD2_0
			var hasLock = !keyLocks.ContainsKey(cacheKey);
			if (hasLock)
			{
				keyLocks[cacheKey] = new LockState();
			}
			return hasLock;
#elif NETSTANDARD2_1
			return keyLocks.TryAdd(cacheKey, new LockState());
#endif
		}
	}

	public Task<ICacheEntry> WaitAsync(string cacheKey)
	{
		LockState? state;

		lock (keyLocks)
		{
			if (!keyLocks.TryGetValue(cacheKey, out state))
			{
				state = new LockState();
				keyLocks[cacheKey] = state;
			}

			state.CompletionSource ??= new TaskCompletionSource<ICacheEntry>();
			return state.CompletionSource.Task;
		}
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private bool TryRemove(string cacheKey, out LockState? state)
	{
		lock (keyLocks)
		{
#if NETSTANDARD2_0
			if (keyLocks.TryGetValue(cacheKey, out state))
			{
				keyLocks.Remove(cacheKey);
				return true;
			}
			return false;
#elif NETSTANDARD2_1
			return keyLocks.Remove(cacheKey, out state);
#endif
		}
	}

	public void ReleaseLock(string cacheKey, ICacheEntry cacheEntry)
	{
		if (TryRemove(cacheKey, out var state))
		{
			state?.CompletionSource?.TrySetResult(cacheEntry);
		}
	}

	public void ReleaseLock(string cacheKey, Exception exception)
	{
		if (TryRemove(cacheKey, out var state))
		{
			state?.CompletionSource?.SetException(exception);
		}
	}

	/// <summary>
	/// Marks the in-flight work for <paramref name="cacheKey"/> (if any) as evicted so a concurrently completing refresh does not resurrect the entry.
	/// </summary>
	public void MarkEvicted(string cacheKey)
	{
		lock (keyLocks)
		{
			if (keyLocks.TryGetValue(cacheKey, out var state))
			{
				state.Evicted = true;
			}
		}
	}

	/// <summary>
	/// Marks all in-flight work as evicted so concurrently completing refreshes do not resurrect entries after a flush.
	/// </summary>
	public void MarkAllEvicted()
	{
		lock (keyLocks)
		{
			foreach (var state in keyLocks.Values)
			{
				state.Evicted = true;
			}
		}
	}

	/// <summary>
	/// Returns whether the in-flight work for <paramref name="cacheKey"/> was marked as evicted.
	/// </summary>
	public bool WasEvicted(string cacheKey)
	{
		lock (keyLocks)
		{
			return keyLocks.TryGetValue(cacheKey, out var state) && state.Evicted;
		}
	}
}
