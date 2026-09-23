using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace CacheTower.Internal;

internal readonly struct CacheEntryKeyLock
{
	private readonly Dictionary<string, TaskCompletionSource<CacheRefreshCompletion>?> keyLocks = new(StringComparer.Ordinal);

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

	public Task<CacheRefreshCompletion> WaitAsync(string cacheKey, CancellationToken cancellationToken = default)
	{
		TaskCompletionSource<CacheRefreshCompletion>? completionSource;

		lock (keyLocks)
		{
			if (!keyLocks.TryGetValue(cacheKey, out completionSource) || completionSource == null)
			{
				completionSource = new TaskCompletionSource<CacheRefreshCompletion>();
				keyLocks[cacheKey] = completionSource;
			}
		}

		return WaitWithCancellationAsync(completionSource.Task, cancellationToken);
	}

	private static async Task<CacheRefreshCompletion> WaitWithCancellationAsync(
		Task<CacheRefreshCompletion> completionTask,
		CancellationToken cancellationToken)
	{
		if (!cancellationToken.CanBeCanceled)
		{
			return await completionTask.ConfigureAwait(false);
		}

		var cancellationTaskSource = new TaskCompletionSource<bool>();
		using (cancellationToken.Register(state => ((TaskCompletionSource<bool>)state!).TrySetResult(true), cancellationTaskSource))
		{
			if (cancellationToken.IsCancellationRequested)
			{
				throw new OperationCanceledException(cancellationToken);
			}

			var completedTask = await Task.WhenAny(completionTask, cancellationTaskSource.Task).ConfigureAwait(false);
			if (ReferenceEquals(completedTask, completionTask))
			{
				return await completionTask.ConfigureAwait(false);
			}

			throw new OperationCanceledException(cancellationToken);
		}
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private bool TryRemove(string cacheKey, out TaskCompletionSource<CacheRefreshCompletion>? completionSource)
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

	public void ReleaseLock(string cacheKey, CacheRefreshCompletion completion)
	{
		if (TryRemove(cacheKey, out var completionSource))
		{
			completionSource?.TrySetResult(completion);
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
