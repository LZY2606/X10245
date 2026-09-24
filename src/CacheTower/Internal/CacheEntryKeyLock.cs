using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace CacheTower.Internal;

internal sealed class CacheEntryKeyLock
{
	private sealed class LockState
	{
		public TaskCompletionSource<ICacheEntry>? RefreshCompletion;
		public long Generation;
		public bool IsActive;
		public bool IsEvicting;
		public bool EvictionStarted;
		public TaskCompletionSource<object?>? EvictionCompletion;
	}

	private readonly Dictionary<string, LockState> keyLocks = new(StringComparer.Ordinal);

	public CacheEntryKeyLock() { }

	public bool AcquireLock(string cacheKey, out long generation)
	{
		lock (keyLocks)
		{
			if (!keyLocks.TryGetValue(cacheKey, out var state))
			{
				state = new LockState();
				keyLocks[cacheKey] = state;
			}

			if (state.IsActive || state.IsEvicting)
			{
				generation = state.Generation;
				return false;
			}

			state.IsActive = true;
			state.RefreshCompletion = null;
			generation = state.Generation;
			return true;
		}
	}

	public Task<ICacheEntry> WaitAsync(string cacheKey)
	{
		lock (keyLocks)
		{
			if (!keyLocks.TryGetValue(cacheKey, out var state) || !state.IsActive)
			{
				return Task.FromResult<ICacheEntry>(new CacheEntry<object>(null, DateTime.MinValue));
			}

			var completionSource = state.RefreshCompletion;
			if (completionSource == null)
			{
				completionSource = new TaskCompletionSource<ICacheEntry>(TaskCreationOptions.RunContinuationsAsynchronously);
				state.RefreshCompletion = completionSource;
			}

			return completionSource.Task;
		}
	}

	public long IncrementGeneration(string cacheKey)
	{
		lock (keyLocks)
		{
			if (!keyLocks.TryGetValue(cacheKey, out var state))
			{
				state = new LockState();
				keyLocks[cacheKey] = state;
			}

			return ++state.Generation;
		}
	}

	public bool IsEvictionInProgress(string cacheKey, long generation)
	{
		lock (keyLocks)
		{
			return keyLocks.TryGetValue(cacheKey, out var state) &&
				state.IsActive &&
				state.IsEvicting &&
				state.Generation == generation;
		}
	}

	public bool BeginEviction(string cacheKey, out long generation, out Task activeOperation)
	{
		lock (keyLocks)
		{
			if (!keyLocks.TryGetValue(cacheKey, out var state))
			{
				state = new LockState();
				keyLocks[cacheKey] = state;
			}

			if (state.IsActive)
			{
				state.IsEvicting = true;
				state.EvictionStarted = false;
				if (state.RefreshCompletion == null)
				{
					state.RefreshCompletion = new TaskCompletionSource<ICacheEntry>(TaskCreationOptions.RunContinuationsAsynchronously);
				}

				generation = state.Generation;
				activeOperation = state.RefreshCompletion.Task.ContinueWith(_ => { }, TaskScheduler.Default);
				return false;
			}

			if (state.IsEvicting && state.EvictionStarted)
			{
				generation = state.Generation;
				activeOperation = state.EvictionCompletion?.Task ?? Task.FromResult<object?>(null);
				return false;
			}

			if (state.IsEvicting)
			{
				state.EvictionStarted = true;
				state.EvictionCompletion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
				generation = state.Generation;
				activeOperation = Task.CompletedTask;
				return true;
			}

			state.IsEvicting = true;
			state.EvictionStarted = true;
			state.RefreshCompletion = null;
			state.EvictionCompletion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
			generation = ++state.Generation;
			return true;
		}
	}

	public void EndEviction(string cacheKey, long generation)
	{
		lock (keyLocks)
		{
			if (keyLocks.TryGetValue(cacheKey, out var state) &&
				state.IsEvicting &&
				state.Generation == generation)
			{
				state.EvictionCompletion?.TrySetResult(null);
				state.IsEvicting = false;
				state.EvictionStarted = false;
				state.EvictionCompletion = null;
				state.RefreshCompletion = null;
				keyLocks.Remove(cacheKey);
			}
		}
	}

	private bool TryRelease(string cacheKey, long generation, out TaskCompletionSource<ICacheEntry>? completionSource)
	{
		lock (keyLocks)
		{
			if (!keyLocks.TryGetValue(cacheKey, out var state) ||
				!state.IsActive ||
				state.Generation != generation)
			{
				completionSource = null;
				return false;
			}

			completionSource = state.RefreshCompletion;
			state.IsActive = false;
			state.RefreshCompletion = null;
			if (!state.IsEvicting)
			{
				keyLocks.Remove(cacheKey);
			}
			return true;
		}
	}

	public void ReleaseLock(string cacheKey, long generation, ICacheEntry cacheEntry)
	{
		if (TryRelease(cacheKey, generation, out var completionSource))
		{
			completionSource?.TrySetResult(cacheEntry);
		}
	}

	public void ReleaseLockForEviction(string cacheKey, long generation)
	{
		if (TryRelease(cacheKey, generation, out var completionSource))
		{
			completionSource?.TrySetResult(new CacheEntry<object>(null, DateTime.MinValue));
		}
	}

	public void ReleaseLock(string cacheKey, long generation, Exception exception)
	{
		if (TryRelease(cacheKey, generation, out var completionSource))
		{
			completionSource?.TrySetException(exception);
		}
	}
}
