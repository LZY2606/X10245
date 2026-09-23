using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace CacheTower.Internal;

internal readonly struct CacheEntryKeyLock
{
	private readonly Dictionary<string, TaskCompletionSource<ICacheEntry>?> keyLocks = new(StringComparer.Ordinal);

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

	public Task<ICacheEntry> WaitAsync(string cacheKey, CancellationToken cancellationToken = default)
	{
		TaskCompletionSource<ICacheEntry>? completionSource;

		lock (keyLocks)
		{
			if (!keyLocks.TryGetValue(cacheKey, out completionSource) || completionSource == null)
			{
				completionSource = new TaskCompletionSource<ICacheEntry>();
				keyLocks[cacheKey] = completionSource;
			}
		}

		if (!cancellationToken.CanBeCanceled)
		{
			return completionSource.Task;
		}

		if (cancellationToken.IsCancellationRequested)
		{
			return Task.FromCanceled<ICacheEntry>(cancellationToken);
		}

		return WaitWithCancellationAsync(completionSource, cancellationToken);
	}

	private static async Task<ICacheEntry> WaitWithCancellationAsync(
		TaskCompletionSource<ICacheEntry> sharedSource,
		CancellationToken cancellationToken)
	{
		//Each cancelling waiter gets its own completion source so a caller cancelling their wait
		//never cancels or faults the shared refresh other callers are waiting on.
		var waiterSource = new TaskCompletionSource<ICacheEntry>(TaskCreationOptions.RunContinuationsAsynchronously);

		using (cancellationToken.Register(static state =>
		{
			var source = (TaskCompletionSource<ICacheEntry>)state!;
			source.TrySetCanceled();
		}, waiterSource))
		{
			var sharedTask = sharedSource.Task;
			sharedTask.ContinueWith(
				static (task, state) =>
				{
					var source = (TaskCompletionSource<ICacheEntry>)state!;
					if (task.IsCanceled)
					{
						source.TrySetCanceled();
					}
					else if (task.IsFaulted)
					{
						source.TrySetException(task.Exception!.InnerException ?? task.Exception);
					}
					else
					{
						source.TrySetResult(task.Result);
					}
				},
				waiterSource,
				CancellationToken.None,
				TaskContinuationOptions.ExecuteSynchronously,
				TaskScheduler.Default
			);

			return await waiterSource.Task.ConfigureAwait(false);
		}
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private bool TryRemove(string cacheKey, out TaskCompletionSource<ICacheEntry>? completionSource)
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
