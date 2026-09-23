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

	public Task<ICacheEntry> WaitAsync(string cacheKey)
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

		return completionSource.Task;
	}

	/// <summary>
	/// Waits for the lock to be released, observing <paramref name="cancellationToken"/> for the caller's wait only.
	/// </summary>
	/// <remarks>
	/// Cancellation abandons the caller's wait - the shared refresh that owns the lock continues unaffected and later
	/// releases the lock (and any other waiters) in the usual way.
	/// </remarks>
	public async Task<ICacheEntry> WaitAsync(string cacheKey, CancellationToken cancellationToken)
	{
		var waitTask = WaitAsync(cacheKey);
		var cancellationTaskSource = new TaskCompletionSource<ICacheEntry>(TaskCreationOptions.RunContinuationsAsynchronously);
		using var registration = cancellationToken.Register(static state =>
		{
			((TaskCompletionSource<ICacheEntry>)state!).TrySetCanceled();
		}, cancellationTaskSource);

		var completedTask = await Task.WhenAny(waitTask, cancellationTaskSource.Task).ConfigureAwait(false);
		if (completedTask == waitTask)
		{
			return await waitTask.ConfigureAwait(false);
		}

		//Continue observing the refresh so an in-flight factory failure can't become an unobserved exception.
		_ = waitTask.ContinueWith(static task =>
		{
			if (task.IsFaulted)
			{
				_ = task.Exception;
			}
		}, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

		//Propagates a TaskCanceledException while the shared refresh keeps running.
		return await cancellationTaskSource.Task.ConfigureAwait(false);
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
