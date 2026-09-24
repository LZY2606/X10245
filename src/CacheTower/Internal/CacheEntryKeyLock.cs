using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CacheTower.Internal;

internal readonly struct RefreshStart
{
	public RefreshStart(bool isLeader, long generation, Task<ICacheRefreshResult>? refreshTask)
	{
		IsLeader = isLeader;
		Generation = generation;
		RefreshTask = refreshTask;
	}

	public bool IsLeader { get; }
	public long Generation { get; }
	public Task<ICacheRefreshResult>? RefreshTask { get; }
}

internal sealed class CacheEntryKeyLock
{
	private sealed class KeyLockState
	{
		public SemaphoreSlim Semaphore { get; } = new(1, 1);
		public TaskCompletionSource<ICacheRefreshResult>? RefreshCompletion { get; set; }
		public int Operations { get; set; }
		public bool RefreshRunning { get; set; }
		public long Generation { get; set; }
	}

	private sealed class ExclusiveLockReleaser : IDisposable
	{
		private readonly CacheEntryKeyLock owner;
		private readonly string cacheKey;
		private readonly KeyLockState state;
		private bool disposed;

		public ExclusiveLockReleaser(CacheEntryKeyLock owner, string cacheKey, KeyLockState state)
		{
			this.owner = owner;
			this.cacheKey = cacheKey;
			this.state = state;
		}

		public void Dispose()
		{
			if (disposed)
			{
				return;
			}

			disposed = true;
			state.Semaphore.Release();
			owner.EndOperation(cacheKey, state);
		}
	}

	private readonly Dictionary<string, KeyLockState> keyLocks = new(StringComparer.Ordinal);

	public RefreshStart BeginRefresh(string cacheKey)
	{
		lock (keyLocks)
		{
			if (!keyLocks.TryGetValue(cacheKey, out var state))
			{
				state = new KeyLockState();
				keyLocks.Add(cacheKey, state);
			}

			if (state.RefreshRunning)
			{
				return new RefreshStart(false, state.Generation, state.RefreshCompletion!.Task);
			}

			state.RefreshRunning = true;
			state.RefreshCompletion = new TaskCompletionSource<ICacheRefreshResult>(TaskCreationOptions.RunContinuationsAsynchronously);
			state.Operations++;
			return new RefreshStart(true, state.Generation, null);
		}
	}

	public async Task<IDisposable> AcquireExclusiveAsync(string cacheKey)
	{
		KeyLockState state;

		lock (keyLocks)
		{
			if (!keyLocks.TryGetValue(cacheKey, out state))
			{
				state = new KeyLockState();
				keyLocks.Add(cacheKey, state);
			}

			state.Operations++;
		}

		await state.Semaphore.WaitAsync().ConfigureAwait(false);

		lock (keyLocks)
		{
			state.Generation++;
		}

		return new ExclusiveLockReleaser(this, cacheKey, state);
	}

	public bool TryAcquireForBackPopulation(string cacheKey, out long generation)
	{
		lock (keyLocks)
		{
			if (!keyLocks.TryGetValue(cacheKey, out var state))
			{
				state = new KeyLockState();
				keyLocks.Add(cacheKey, state);
			}

			if (state.RefreshRunning || !state.Semaphore.Wait(0))
			{
				generation = default;
				return false;
			}

			state.Operations++;
			generation = state.Generation;
			return true;
		}
	}

	public void ReleaseBackPopulation(string cacheKey, long generation)
	{
		if (!keyLocks.TryGetValue(cacheKey, out var state))
		{
			return;
		}

		state.Semaphore.Release();
		EndOperation(cacheKey, state);
	}

	public async Task<ICacheRefreshResult> WaitForRefreshAsync(string cacheKey, Task<ICacheRefreshResult> refreshTask, CancellationToken cancellationToken)
	{
		var cancellationCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		using (cancellationToken.Register(state => ((TaskCompletionSource<bool>)state!).TrySetResult(true), cancellationCompletion))
		{
			var completedTask = await Task.WhenAny(refreshTask, cancellationCompletion.Task).ConfigureAwait(false);
			if (completedTask == cancellationCompletion.Task)
			{
				throw new TaskCanceledException();
			}
		}

		return await refreshTask.ConfigureAwait(false);
	}

	public void CompleteRefresh(string cacheKey, ICacheRefreshResult result)
	{
		if (TryEndRefresh(cacheKey, out var state, out var completion))
		{
			completion.TrySetResult(result);
			state.Semaphore.Release();
			EndOperation(cacheKey, state);
		}
	}

	public void FailRefresh(string cacheKey, Exception exception)
	{
		if (TryEndRefresh(cacheKey, out var state, out var completion))
		{
			completion.TrySetException(exception);
			state.Semaphore.Release();
			EndOperation(cacheKey, state);
		}
	}

	public void CancelRefresh(string cacheKey)
	{
		if (TryEndRefresh(cacheKey, out var state, out var completion))
		{
			completion.TrySetCanceled();
			state.Semaphore.Release();
			EndOperation(cacheKey, state);
		}
	}

	public bool HasGenerationChanged(string cacheKey, long expectedGeneration)
	{
		lock (keyLocks)
		{
			return keyLocks.TryGetValue(cacheKey, out var state) && state.Generation != expectedGeneration;
		}
	}

	private bool TryEndRefresh(string cacheKey, out KeyLockState state, out TaskCompletionSource<ICacheRefreshResult> completion)
	{
		lock (keyLocks)
		{
			if (keyLocks.TryGetValue(cacheKey, out state!) && state.RefreshRunning)
			{
				state.RefreshRunning = false;
				completion = state.RefreshCompletion!;
				state.RefreshCompletion = null;
				return true;
			}

			completion = null!;
			state = null!;
			return false;
		}
	}

	private void EndOperation(string cacheKey, KeyLockState expectedState)
	{
		lock (keyLocks)
		{
			if (!keyLocks.TryGetValue(cacheKey, out var state) || !ReferenceEquals(state, expectedState))
			{
				return;
			}

			state.Operations--;
			if (state.Operations == 0 && !state.RefreshRunning && state.RefreshCompletion == null && state.Semaphore.CurrentCount == 1)
			{
				keyLocks.Remove(cacheKey);
				state.Semaphore.Dispose();
			}
		}
	}
}
