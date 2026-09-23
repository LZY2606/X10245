using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CacheTower.Internal;

internal readonly struct CacheRefreshToken
{
	public bool IsAcquired { get; }
	public long EvictionVersion { get; }
	public long SetVersion { get; }

	public CacheRefreshToken(bool isAcquired, long evictionVersion, long setVersion)
	{
		IsAcquired = isAcquired;
		EvictionVersion = evictionVersion;
		SetVersion = setVersion;
	}
}

internal sealed class CacheLockState
{
	public bool IsRefreshing { get; set; }
	public long EvictionVersion { get; set; }
	public long SetVersion { get; set; }
	public TaskCompletionSource<ICacheEntry>? CompletionSource { get; set; }
}

internal sealed class CacheEntryKeyLock
{
	private readonly Dictionary<string, CacheLockState> keyLocks = new(StringComparer.Ordinal);

	public CacheRefreshToken AcquireRefresh(string cacheKey)
	{
		lock (keyLocks)
		{
			if (!keyLocks.TryGetValue(cacheKey, out var state))
			{
				state = new CacheLockState();
				keyLocks[cacheKey] = state;
			}

			if (state.IsRefreshing)
			{
				return new CacheRefreshToken(false, state.EvictionVersion, state.SetVersion);
			}

			state.IsRefreshing = true;
			state.CompletionSource = null;
			return new CacheRefreshToken(true, state.EvictionVersion, state.SetVersion);
		}
	}

	public Task<ICacheEntry> WaitAsync(string cacheKey, CancellationToken cancellationToken)
	{
		TaskCompletionSource<ICacheEntry> completionSource;

		lock (keyLocks)
		{
			if (!keyLocks.TryGetValue(cacheKey, out var state))
			{
				state = new CacheLockState();
				keyLocks[cacheKey] = state;
			}

			completionSource = state.CompletionSource ??= new TaskCompletionSource<ICacheEntry>(TaskCreationOptions.RunContinuationsAsynchronously);
		}

		if (!cancellationToken.CanBeCanceled)
		{
			return completionSource.Task;
		}

		if (cancellationToken.IsCancellationRequested)
		{
			RemoveWaiter(cacheKey, completionSource);
			return Task.FromCanceled<ICacheEntry>(cancellationToken);
		}

		var cancellationRegistration = cancellationToken.Register(stateObject =>
		{
			var (key, source, token) = (Tuple<string, TaskCompletionSource<ICacheEntry>, CancellationToken>)stateObject!;
			if (RemoveWaiter(key, source))
			{
				source.TrySetCanceled(token);
			}
		}, Tuple.Create(cacheKey, completionSource, cancellationToken));

		return completionSource.Task.ContinueWith(
			task =>
			{
				cancellationRegistration.Dispose();
				return task.GetAwaiter().GetResult();
			},
			CancellationToken.None,
			TaskContinuationOptions.ExecuteSynchronously,
			TaskScheduler.Default
		);
	}

	private bool RemoveWaiter(string cacheKey, TaskCompletionSource<ICacheEntry> completionSource)
	{
		lock (keyLocks)
		{
			if (keyLocks.TryGetValue(cacheKey, out var state) && ReferenceEquals(state.CompletionSource, completionSource))
			{
				state.CompletionSource = null;
				return true;
			}

			return false;
		}
	}

	public long NotifyEviction(string cacheKey)
	{
		lock (keyLocks)
		{
			if (!keyLocks.TryGetValue(cacheKey, out var state))
			{
				state = new CacheLockState();
				keyLocks[cacheKey] = state;
			}

			return ++state.EvictionVersion;
		}
	}

	public long NotifyExternalSet(string cacheKey)
	{
		lock (keyLocks)
		{
			if (!keyLocks.TryGetValue(cacheKey, out var state))
			{
				state = new CacheLockState();
				keyLocks[cacheKey] = state;
			}

			return ++state.SetVersion;
		}
	}

	public bool HasEvictionOccurred(CacheRefreshToken refreshToken, string cacheKey)
	{
		lock (keyLocks)
		{
			return !keyLocks.TryGetValue(cacheKey, out var state) || state.EvictionVersion != refreshToken.EvictionVersion;
		}
	}

	public bool HasExternalSetOccurred(CacheRefreshToken refreshToken, string cacheKey)
	{
		lock (keyLocks)
		{
			return !keyLocks.TryGetValue(cacheKey, out var state) || state.SetVersion != refreshToken.SetVersion;
		}
	}

	public bool HasRefresh(string cacheKey)
	{
		lock (keyLocks)
		{
			return keyLocks.TryGetValue(cacheKey, out var state) && state.IsRefreshing;
		}
	}

	public void ReleaseRefresh(
		string cacheKey,
		CacheRefreshToken refreshToken,
		ICacheEntry? cacheEntry,
		Exception? exception = null
	)
	{
		TaskCompletionSource<ICacheEntry>? completionSource;

		lock (keyLocks)
		{
			if (!keyLocks.TryGetValue(cacheKey, out var state) || !state.IsRefreshing)
			{
				return;
			}

			state.IsRefreshing = false;
			completionSource = state.CompletionSource;

			var canRemoveState = state.EvictionVersion == refreshToken.EvictionVersion
				&& state.SetVersion == refreshToken.SetVersion
				&& completionSource == null;

			if (canRemoveState)
			{
				keyLocks.Remove(cacheKey);
			}
		}

		if (completionSource != null)
		{
			if (exception != null)
			{
				completionSource.TrySetException(exception);
			}
			else
			{
				completionSource.TrySetResult(cacheEntry!);
			}
		}
	}
}
