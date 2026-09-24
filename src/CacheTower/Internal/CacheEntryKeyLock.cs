using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
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
			if (!keyLocks.TryGetValue(cacheKey, out state) || state.CompletionSource == null)
			{
				state ??= new LockState();
				state.CompletionSource = new TaskCompletionSource<ICacheEntry>();
				keyLocks[cacheKey] = state;
			}
		}

		return state.CompletionSource.Task;
	}

	/// <summary>
	/// Waits for the in-flight refresh for <paramref name="cacheKey"/> to complete, without cancelling that shared refresh when the
	/// <paramref name="cancellationToken"/> is cancelled. Cancellation only abandons this caller's wait.
	/// </summary>
	public async Task<ICacheEntry> WaitAsync(string cacheKey, CancellationToken cancellationToken)
	{
		var waitTask = WaitAsync(cacheKey);
		if (!cancellationToken.CanBeCanceled)
		{
			return await waitTask.ConfigureAwait(false);
		}

		var cancellationSource = new TaskCompletionSource<ICacheEntry>(TaskCreationOptions.RunContinuationsAsynchronously);
		using (cancellationToken.Register(() => cancellationSource.TrySetCanceled(cancellationToken)))
		{
			var completedTask = await Task.WhenAny(waitTask, cancellationSource.Task).ConfigureAwait(false);
			return await completedTask.ConfigureAwait(false);
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
	/// Marks the in-flight refresh for <paramref name="cacheKey"/> (if any) as evicted, so its completion does not resurrect the entry.
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
	/// Marks all in-flight refreshes as evicted, so their completion does not resurrect entries after a flush.
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
	/// Whether the in-flight refresh for <paramref name="cacheKey"/> has been marked as evicted.
	/// </summary>
	public bool IsMarkedEvicted(string cacheKey)
	{
		lock (keyLocks)
		{
			return keyLocks.TryGetValue(cacheKey, out var state) && state.Evicted;
		}
	}
}
