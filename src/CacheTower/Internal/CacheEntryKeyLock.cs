using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace CacheTower.Internal;

internal readonly struct CacheEntryKeyLock
{
	private sealed class KeyState
	{
		public TaskCompletionSource<ICacheEntry>? CompletionSource;
		public bool EvictionBarrier;
	}

	private readonly object SyncLock = new();
	private readonly Dictionary<string, KeyState> keyStates = new(StringComparer.Ordinal);
	private readonly Dictionary<string, int> evictionGenerations = new(StringComparer.Ordinal);

	public CacheEntryKeyLock() { }

	/// <summary>
	/// Marker published to waiters when an eviction barrier lifts so they can distinguish "key was deleted" from a real entry.
	/// </summary>
	internal sealed class EvictionSentinel : ICacheEntry
	{
		public static readonly EvictionSentinel Instance = new();
		public DateTime Expiry => DateTime.MinValue;
	}

	public bool AcquireLock(string cacheKey)
	{
		lock (SyncLock)
		{
			if (keyStates.ContainsKey(cacheKey))
			{
				//Either a refresh owns the key or an eviction barrier is active - callers must wait.
				return false;
			}

			keyStates[cacheKey] = new KeyState();
			return true;
		}
	}

	public Task<ICacheEntry> WaitAsync(string cacheKey)
	{
		lock (SyncLock)
		{
			if (!keyStates.TryGetValue(cacheKey, out var state))
			{
				state = new KeyState();
				keyStates[cacheKey] = state;
			}

			if (state.CompletionSource == null)
			{
				state.CompletionSource = new TaskCompletionSource<ICacheEntry>(TaskCreationOptions.RunContinuationsAsynchronously);
			}

			return state.CompletionSource.Task;
		}
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private bool TryRemove(string cacheKey, out TaskCompletionSource<ICacheEntry>? completionSource)
	{
		lock (SyncLock)
		{
			if (keyStates.TryGetValue(cacheKey, out var state) && !state.EvictionBarrier)
			{
				completionSource = state.CompletionSource;
				keyStates.Remove(cacheKey);
				return true;
			}

			completionSource = null;
			return false;
		}
	}

	public void ReleaseLock(string cacheKey, ICacheEntry cacheEntry)
	{
		TaskCompletionSource<ICacheEntry>? completionSource;
		lock (SyncLock)
		{
			if (keyStates.TryGetValue(cacheKey, out var state) && !state.EvictionBarrier)
			{
				completionSource = state.CompletionSource;
				keyStates.Remove(cacheKey);
			}
			else
			{
				completionSource = null;
			}
		}

		completionSource?.TrySetResult(cacheEntry);
	}

	public void ReleaseLock(string cacheKey, Exception exception)
	{
		TaskCompletionSource<ICacheEntry>? completionSource;
		lock (SyncLock)
		{
			if (keyStates.TryGetValue(cacheKey, out var state) && !state.EvictionBarrier)
			{
				completionSource = state.CompletionSource;
				keyStates.Remove(cacheKey);
			}
			else
			{
				completionSource = null;
			}
		}

		completionSource?.SetException(exception);
	}

	/// <summary>
	/// Captures the current eviction generation for a key.
	/// A refresh commits its result only if the generation is unchanged when it finishes, which prevents
	/// a refresh completing concurrently with an eviction from resurrecting the removed value.
	/// </summary>
	public int CaptureEvictionGeneration(string cacheKey)
	{
		lock (SyncLock)
		{
			return evictionGenerations.TryGetValue(cacheKey, out var generation) ? generation : 0;
		}
	}

	/// <summary>
	/// Returns whether the eviction generation for <paramref name="cacheKey"/> has advanced since <paramref name="capturedGeneration"/>.
	/// </summary>
	public bool WasEvictedSince(string cacheKey, int capturedGeneration)
	{
		lock (SyncLock)
		{
			var currentGeneration = evictionGenerations.TryGetValue(cacheKey, out var generation) ? generation : 0;
			return currentGeneration != capturedGeneration;
		}
	}

	/// <summary>
	/// Runs <paramref name="evictAction"/> in coordination with in-flight refreshes for <paramref name="cacheKey"/>.
	/// The eviction registers a barrier under the same monitor refreshes use and bumps the eviction generation, so either
	/// the eviction completes before a refresh commits (the refresh observes the new generation and discards its result) or
	/// the refresh commits before the barrier is registered (the eviction then removes that value). The old value can never
	/// be written back after it has been evicted.
	/// </summary>
	public async ValueTask EvictCoordinatedAsync(string cacheKey, Func<ValueTask> evictAction)
	{
		TaskCompletionSource<ICacheEntry> completionSource;

		lock (SyncLock)
		{
			evictionGenerations[cacheKey] = (evictionGenerations.TryGetValue(cacheKey, out var generation) ? generation : 0) + 1;

			if (keyStates.TryGetValue(cacheKey, out var existingState))
			{
				existingState.EvictionBarrier = true;
				if (existingState.CompletionSource == null)
				{
					existingState.CompletionSource = new TaskCompletionSource<ICacheEntry>(TaskCreationOptions.RunContinuationsAsynchronously);
				}
				completionSource = existingState.CompletionSource;
			}
			else
			{
				var state = new KeyState
				{
					EvictionBarrier = true,
					CompletionSource = new TaskCompletionSource<ICacheEntry>(TaskCreationOptions.RunContinuationsAsynchronously)
				};
				keyStates[cacheKey] = state;
				completionSource = state.CompletionSource;
			}
		}

		try
		{
			await evictAction().ConfigureAwait(false);
		}
		finally
		{
			lock (SyncLock)
			{
				keyStates.Remove(cacheKey);
			}

			completionSource.TrySetResult(EvictionSentinel.Instance);
		}
	}
}
