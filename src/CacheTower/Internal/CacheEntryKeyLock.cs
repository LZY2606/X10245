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

	/// <summary>
	/// Whether a refresh (owner or waiting callers) is currently registered for <paramref name="cacheKey"/>.
	/// </summary>
	public bool IsLocked(string cacheKey)
	{
		lock (keyLocks)
		{
			return keyLocks.ContainsKey(cacheKey);
		}
	}

	/// <summary>
	/// Whether at least one caller is currently waiting to be released for <paramref name="cacheKey"/>.
	/// </summary>
	/// <remarks>
	/// Test-only observation point for synchronizing deterministic concurrency tests. It does not alter locking behaviour.
	/// </remarks>
	public bool HasWaiter(string cacheKey)
	{
		lock (keyLocks)
		{
			return keyLocks.TryGetValue(cacheKey, out var completionSource) && completionSource != null;
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
	/// Waits for the shared refresh to finish, observing <paramref name="cancellationToken"/> without cancelling the refresh itself.
	/// </summary>
	/// <remarks>
	/// Cancellation only completes this caller's wait - the shared <see cref="TaskCompletionSource{TResult}"/> is never cancelled,
	/// so the in-flight refresh and every other waiter are unaffected.
	/// </remarks>
	public async Task<ICacheEntry> WaitAsync(string cacheKey, CancellationToken cancellationToken)
	{
		var refreshTask = WaitAsync(cacheKey);

		if (!cancellationToken.CanBeCanceled || refreshTask.IsCompleted)
		{
			return await refreshTask.ConfigureAwait(false);
		}

		var cancellationCompletion = new TaskCompletionSource<ICacheEntry>(TaskCreationOptions.RunContinuationsAsynchronously);
		using (cancellationToken.Register(static state => ((TaskCompletionSource<ICacheEntry>)state!).TrySetCanceled(), cancellationCompletion))
		{
			var completedTask = await Task.WhenAny(refreshTask, cancellationCompletion.Task).ConfigureAwait(false);
			return await completedTask.ConfigureAwait(false);
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
