using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace CacheTower.Internal;

internal readonly struct KeyLockState
{
	public KeyLockState(TaskCompletionSource<ICacheEntry>? completionSource, long evictionGeneration)
	{
		CompletionSource = completionSource;
		EvictionGeneration = evictionGeneration;
	}

	public TaskCompletionSource<ICacheEntry>? CompletionSource { get; }
	public long EvictionGeneration { get; }
}

internal readonly struct CacheEntryKeyLock
{
	private readonly Dictionary<string, KeyLockState> keyLocks = new(StringComparer.Ordinal);

	public CacheEntryKeyLock() { }

	public bool AcquireLock(string cacheKey, out long evictionGeneration)
	{
		lock (keyLocks)
		{
#if NETSTANDARD2_0
			var hasLock = !keyLocks.ContainsKey(cacheKey);
			if (hasLock)
			{
				keyLocks[cacheKey] = new KeyLockState(null, 0);
			}
			evictionGeneration = hasLock ? 0 : keyLocks[cacheKey].EvictionGeneration;
			return hasLock;
#elif NETSTANDARD2_1
			ref var state = ref CollectionsMarshal.GetValueRefOrAddDefault(keyLocks, cacheKey, out var exists);
			evictionGeneration = state.EvictionGeneration;
			return !exists;
#endif
		}
	}

	public Task<ICacheEntry> WaitAsync(string cacheKey)
	{
		lock (keyLocks)
		{
			if (!keyLocks.TryGetValue(cacheKey, out var state))
			{
				state = new KeyLockState(new TaskCompletionSource<ICacheEntry>(), 0);
				keyLocks[cacheKey] = state;
			}
			else if (state.CompletionSource == null)
			{
				keyLocks[cacheKey] = new KeyLockState(new TaskCompletionSource<ICacheEntry>(), state.EvictionGeneration);
				state = keyLocks[cacheKey];
			}

			return state.CompletionSource!.Task;
		}
	}

	/// <summary>
	/// Waits for the current refresh of <paramref name="cacheKey"/> to complete without joining the shared refresh.
	/// Cancelling <paramref name="cancellationToken"/> abandons only this caller's wait - the refresh itself continues.
	/// </summary>
	public async Task<ICacheEntry> WaitAsync(string cacheKey, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();

		var waitTask = WaitAsync(cacheKey);
		if (waitTask.IsCompleted || !cancellationToken.CanBeCanceled)
		{
			return await waitTask.ConfigureAwait(false);
		}

		var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		using (cancellationToken.Register(static state => ((TaskCompletionSource<bool>)state!).TrySetResult(true), tcs))
		{
			if (waitTask == await Task.WhenAny(waitTask, tcs.Task).ConfigureAwait(false))
			{
				return await waitTask.ConfigureAwait(false);
			}
		}

		cancellationToken.ThrowIfCancellationRequested();
		return await waitTask.ConfigureAwait(false);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private bool TryRemove(string cacheKey, out TaskCompletionSource<ICacheEntry>? completionSource)
	{
		lock (keyLocks)
		{
			if (keyLocks.TryGetValue(cacheKey, out var state))
			{
				completionSource = state.CompletionSource;
				keyLocks.Remove(cacheKey);
				return true;
			}

			completionSource = null;
			return false;
		}
	}

	/// <summary>
	/// Marks the key as evicted. Any in-flight refresh or back-population must not repopulate the cache afterwards.
	/// </summary>
	public void MarkEvicted(string cacheKey)
	{
		lock (keyLocks)
		{
			if (keyLocks.TryGetValue(cacheKey, out var state))
			{
				keyLocks[cacheKey] = new KeyLockState(state.CompletionSource, unchecked(state.EvictionGeneration + 1));
			}
			else
			{
				keyLocks[cacheKey] = new KeyLockState(null, 1);
			}
		}
	}

	/// <summary>
	/// Removes the eviction marker left behind by an <see cref="MarkEvicted"/> call that happened when no refresh was in flight.
	/// </summary>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public void ClearEvictionMarker(string cacheKey)
	{
		lock (keyLocks)
		{
			if (keyLocks.TryGetValue(cacheKey, out var state) && state.CompletionSource == null && state.EvictionGeneration > 0)
			{
				keyLocks.Remove(cacheKey);
			}
		}
	}

	/// <summary>
	/// Returns whether the key was evicted since <paramref name="observedEvictionGeneration"/> was observed.
	/// </summary>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public bool WasEvictedSince(string cacheKey, long observedEvictionGeneration)
	{
		lock (keyLocks)
		{
			return keyLocks.TryGetValue(cacheKey, out var state) && state.EvictionGeneration != observedEvictionGeneration;
		}
	}

	/// <summary>
	/// Whether a refresh or back-population is currently in flight for the key. Intended for diagnostics and tests.
	/// </summary>
	public bool IsLocked(string cacheKey)
	{
		lock (keyLocks)
		{
			return keyLocks.ContainsKey(cacheKey);
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
