using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CacheTower.Internal;

internal sealed class CacheEntryKeyLock
{
	private sealed class KeyState
	{
		public bool IsRefreshRunning;
		public int EvictionGeneration;
		public TaskCompletionSource<ICacheEntry>? RefreshCompletion;
		public SemaphoreSlim? Gate;
		public int OperationCount;
	}

	private readonly Dictionary<string, KeyState> keyLocks = new(StringComparer.Ordinal);

	public async Task<CacheRefreshLease?> TryBeginRefreshAsync(string cacheKey)
	{
		SemaphoreSlim gate;
		int generation;

		lock (keyLocks)
		{
			if (!keyLocks.TryGetValue(cacheKey, out var state))
			{
				state = new KeyState();
				keyLocks[cacheKey] = state;
			}

			if (state.IsRefreshRunning)
			{
				return null;
			}

			state.IsRefreshRunning = true;
			state.OperationCount++;
			generation = state.EvictionGeneration;
			gate = state.Gate ??= new SemaphoreSlim(1, 1);
		}

		await gate.WaitAsync().ConfigureAwait(false);
		return new(cacheKey, generation, gate);
	}

	public Task<ICacheEntry> RegisterWaiter(string cacheKey)
	{
		lock (keyLocks)
		{
			if (!keyLocks.TryGetValue(cacheKey, out var state) || !state.IsRefreshRunning)
			{
				throw new InvalidOperationException("No refresh is running for the cache key.");
			}

			state.RefreshCompletion ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
			state.OperationCount++;
			return state.RefreshCompletion.Task;
		}
	}

	public void ReleaseWaiter(string cacheKey)
	{
		lock (keyLocks)
		{
			if (keyLocks.TryGetValue(cacheKey, out var state))
			{
				RemoveOperation(state, cacheKey);
			}
		}
	}

	public bool TryCommitRefresh(CacheRefreshLease lease, ICacheEntry cacheEntry)
	{
		lock (keyLocks)
		{
			if (!keyLocks.TryGetValue(lease.CacheKey, out var state) ||
				!state.IsRefreshRunning ||
				state.EvictionGeneration != lease.EvictionGeneration)
			{
				return false;
			}

			return true;
		}
	}

	public void ReleaseRefreshGate(CacheRefreshLease lease, ICacheEntry cacheEntry)
	{
		TaskCompletionSource<ICacheEntry>? completionSource;

		lock (keyLocks)
		{
			if (!keyLocks.TryGetValue(lease.CacheKey, out var state) || !state.IsRefreshRunning)
			{
				lease.Gate.Release();
				return;
			}

			completionSource = state.RefreshCompletion;
			state.IsRefreshRunning = false;
			state.RefreshCompletion = null;
			RemoveOperation(state, lease.CacheKey);
		}

		lease.Gate.Release();
		completionSource?.TrySetResult(cacheEntry);
	}

	public void CancelRefresh(CacheRefreshLease lease)
	{
		TaskCompletionSource<ICacheEntry>? completionSource;

		lock (keyLocks)
		{
			if (!keyLocks.TryGetValue(lease.CacheKey, out var state) || !state.IsRefreshRunning)
			{
				lease.Gate.Release();
				return;
			}

			completionSource = state.RefreshCompletion;
			state.IsRefreshRunning = false;
			state.RefreshCompletion = null;
			RemoveOperation(state, lease.CacheKey);
		}

		lease.Gate.Release();
		completionSource?.TrySetCanceled();
	}

	public void CompleteRefresh(CacheRefreshLease lease, ICacheEntry cacheEntry)
	{
		if (TryCommitRefresh(lease, cacheEntry))
		{
			ReleaseRefreshGate(lease, cacheEntry);
		}
	}

	public void FailRefresh(CacheRefreshLease lease, Exception exception)
	{
		TaskCompletionSource<ICacheEntry>? completionSource;

		lock (keyLocks)
		{
			if (!keyLocks.TryGetValue(lease.CacheKey, out var state) || !state.IsRefreshRunning)
			{
				lease.Gate.Release();
				return;
			}

			completionSource = state.RefreshCompletion;
			state.IsRefreshRunning = false;
			state.RefreshCompletion = null;
			RemoveOperation(state, lease.CacheKey);
		}

		lease.Gate.Release();
		completionSource?.TrySetException(exception);
	}

	public async Task<CacheEvictionLease> AcquireEvictionAsync(string cacheKey)
	{
		SemaphoreSlim gate;
		int generation;

		lock (keyLocks)
		{
			if (!keyLocks.TryGetValue(cacheKey, out var state))
			{
				state = new KeyState();
				keyLocks[cacheKey] = state;
			}

			generation = ++state.EvictionGeneration;
			gate = state.Gate ??= new SemaphoreSlim(1, 1);
			state.OperationCount++;
		}

		await gate.WaitAsync().ConfigureAwait(false);
		return new(cacheKey, gate, generation);
	}

	public void ReleaseEviction(CacheEvictionLease lease)
	{
		lock (keyLocks)
		{
			if (keyLocks.TryGetValue(lease.CacheKey, out var state))
			{
				lease.Gate.Release();
				RemoveOperation(state, lease.CacheKey);
			}
			else
			{
				lease.Gate.Release();
			}
		}
	}

	private void RemoveOperation(KeyState state, string cacheKey)
	{
		state.OperationCount--;
		if (state.OperationCount <= 0 && !state.IsRefreshRunning && state.RefreshCompletion is null)
		{
			keyLocks.Remove(cacheKey);
			state.Gate?.Dispose();
		}
	}
}

internal readonly struct CacheRefreshLease
{
	public string CacheKey { get; }
	public int EvictionGeneration { get; }
	public SemaphoreSlim Gate { get; }

	public CacheRefreshLease(string cacheKey, int evictionGeneration, SemaphoreSlim gate)
	{
		CacheKey = cacheKey;
		EvictionGeneration = evictionGeneration;
		Gate = gate;
	}
}

internal readonly struct CacheEvictionLease
{
	public string CacheKey { get; }
	public SemaphoreSlim Gate { get; }
	public int Generation { get; }

	public CacheEvictionLease(string cacheKey, SemaphoreSlim gate, int generation)
	{
		CacheKey = cacheKey;
		Gate = gate;
		Generation = generation;
	}
}
