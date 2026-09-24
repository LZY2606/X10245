using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace CacheTower.Internal;

internal sealed class CacheEntryKeyLock
{
	private sealed class KeyLockState
	{
		public SemaphoreSlim MutationLock { get; } = new(1, 1);
		public TaskCompletionSource<CacheRefreshOutcome?>? RefreshCompletion { get; set; }
		public bool RefreshActive { get; set; }
		public int MutationOperations { get; set; }
		public long Version { get; set; }
	}

	internal readonly struct MutationLockLease : IDisposable
	{
		private readonly KeyLockState State;
		private readonly CacheEntryKeyLock Owner;
		private readonly string CacheKey;

		public long Version => State.Version;

		public MutationLockLease(CacheEntryKeyLock owner, string cacheKey, KeyLockState state)
		{
			Owner = owner;
			CacheKey = cacheKey;
			State = state;
		}

		public void Dispose()
		{
			lock (State)
			{
				State.MutationLock.Release();
				State.MutationOperations--;
			}

			Owner.TryRemoveInactive(CacheKey, State);
		}
	}

	private readonly ConcurrentDictionary<string, KeyLockState> KeyLocks = new(StringComparer.Ordinal);

	public bool TryBeginRefresh(string cacheKey, out long version, out Task<CacheRefreshOutcome?> refreshTask)
	{
		var state = KeyLocks.GetOrAdd(cacheKey, static _ => new KeyLockState());
		lock (state)
		{
			version = state.Version;
			if (state.RefreshActive)
			{
				refreshTask = state.RefreshCompletion!.Task;
				return false;
			}

			state.RefreshActive = true;
			state.RefreshCompletion = new TaskCompletionSource<CacheRefreshOutcome?>(TaskCreationOptions.RunContinuationsAsynchronously);
			refreshTask = state.RefreshCompletion.Task;
			return true;
		}
	}

	public async ValueTask<CacheRefreshOutcome?> WaitForRefreshAsync(string cacheKey, CancellationToken cancellationToken)
	{
		if (!KeyLocks.TryGetValue(cacheKey, out var state) || state.RefreshCompletion is null)
		{
			return null;
		}

		var sharedTask = state.RefreshCompletion.Task;
		if (cancellationToken == CancellationToken.None)
		{
			return await sharedTask.ConfigureAwait(false);
		}

		cancellationToken.ThrowIfCancellationRequested();
		var cancellationSource = new TaskCompletionSource<CacheRefreshOutcome?>(TaskCreationOptions.RunContinuationsAsynchronously);
		using var registration = cancellationToken.Register(static state =>
		{
			((TaskCompletionSource<CacheRefreshOutcome?>)state!).TrySetCanceled();
		}, cancellationSource);

		var completedTask = await Task.WhenAny(sharedTask, cancellationSource.Task).ConfigureAwait(false);
		if (!ReferenceEquals(completedTask, sharedTask))
		{
			cancellationToken.ThrowIfCancellationRequested();
		}

		return await sharedTask.ConfigureAwait(false);
	}

	public void CompleteRefresh(string cacheKey, CacheRefreshOutcome outcome)
	{
		if (KeyLocks.TryGetValue(cacheKey, out var state))
		{
			TaskCompletionSource<CacheRefreshOutcome?>? completion;
			lock (state)
			{
				state.RefreshActive = false;
				completion = state.RefreshCompletion;
			}

			completion?.TrySetResult(outcome);
			TryRemoveInactive(cacheKey, state);
		}
	}

	public void CompleteRefresh(string cacheKey, Exception exception)
	{
		if (KeyLocks.TryGetValue(cacheKey, out var state))
		{
			TaskCompletionSource<CacheRefreshOutcome?>? completion;
			lock (state)
			{
				state.RefreshActive = false;
				completion = state.RefreshCompletion;
			}

			completion?.TrySetException(exception);
			TryRemoveInactive(cacheKey, state);
		}
	}

	public async ValueTask<MutationLockLease> AcquireMutationLockAsync(string cacheKey)
	{
		var state = KeyLocks.GetOrAdd(cacheKey, static _ => new KeyLockState());
		lock (state)
		{
			state.MutationOperations++;
		}

		try
		{
			await state.MutationLock.WaitAsync().ConfigureAwait(false);
			return new MutationLockLease(this, cacheKey, state);
		}
		catch
		{
			lock (state)
			{
				state.MutationOperations--;
			}
			throw;
		}
	}

	public async ValueTask<MutationLockLease> AcquireEvictionLockAsync(string cacheKey)
	{
		var lease = await AcquireMutationLockAsync(cacheKey).ConfigureAwait(false);
		var state = KeyLocks[cacheKey];
		lock (state)
		{
			state.Version++;
		}
		return lease;
	}

	private void TryRemoveInactive(string cacheKey, KeyLockState expectedState)
	{
		lock (expectedState)
		{
			if (!expectedState.RefreshActive && expectedState.MutationOperations == 0 && expectedState.MutationLock.CurrentCount == 1)
			{
				((ICollection<KeyValuePair<string, KeyLockState>>)KeyLocks).Remove(new KeyValuePair<string, KeyLockState>(cacheKey, expectedState));
			}
		}
	}

	internal ValueTask WaitForRefreshCompletionAsync(string cacheKey)
	{
		if (KeyLocks.TryGetValue(cacheKey, out var state) && state.RefreshCompletion is not null)
		{
			return new ValueTask(state.RefreshCompletion.Task);
		}

		return default;
	}
}
