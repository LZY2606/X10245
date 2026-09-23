using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace CacheTower.Internal;

internal readonly struct KeyLockEntry
{
	public KeyLockEntry(ulong generation, TaskCompletionSource<ICacheEntry?>? completionSource)
	{
		Generation = generation;
		CompletionSource = completionSource;
	}

	public ulong Generation { get; }
	public TaskCompletionSource<ICacheEntry?>? CompletionSource { get; }
}

/// <summary>
/// A per-key single-flight lock used to co-ordinate refreshes and back-population.
/// </summary>
/// <remarks>
/// Each key tracks a monotonically increasing generation. <see cref="NotifyEviction"/> bumps the generation
/// so an in-flight refresh that completes after an eviction cannot republish the old value.
/// </remarks>
internal sealed class CacheEntryKeyLock
{
	private readonly Dictionary<string, KeyLockEntry> keyLocks = new(StringComparer.Ordinal);

	public CacheEntryKeyLock() { }

	/// <summary>
	/// Attempts to acquire the single-flight lock for <paramref name="cacheKey"/>. When successful, returns the current generation and <see langword="true"/>.
	/// </summary>
	public bool AcquireLock(string cacheKey, out ulong generation)
	{
		lock (keyLocks)
		{
			if (keyLocks.TryGetValue(cacheKey, out var existing))
			{
				generation = existing.Generation;
				return false;
			}

			generation = 1;
			keyLocks[cacheKey] = new KeyLockEntry(generation, null);
			return true;
		}
	}

	/// <summary>
	/// Returns <see langword="true"/> when the key is still at <paramref name="generation"/> and the lock entry belongs to that generation.
	/// </summary>
	public bool IsCurrentGeneration(string cacheKey, ulong generation)
	{
		lock (keyLocks)
		{
			return keyLocks.TryGetValue(cacheKey, out var entry) && entry.Generation == generation;
		}
	}

	public Task<ICacheEntry?> WaitAsync(string cacheKey) => WaitAsync(cacheKey, CancellationToken.None);

	public Task<ICacheEntry?> WaitAsync(string cacheKey, CancellationToken cancellationToken)
	{
		if (cancellationToken.IsCancellationRequested)
		{
			return Task.FromCanceled<ICacheEntry?>(cancellationToken);
		}

		TaskCompletionSource<ICacheEntry?> completionSource;
		lock (keyLocks)
		{
			if (!keyLocks.TryGetValue(cacheKey, out var entry) || entry.CompletionSource == null)
			{
				completionSource = new TaskCompletionSource<ICacheEntry?>(TaskCreationOptions.RunContinuationsAsynchronously);
				keyLocks[cacheKey] = new KeyLockEntry(entry.Generation, completionSource);
			}
			else
			{
				completionSource = entry.CompletionSource;
			}
		}

		if (cancellationToken.CanBeCanceled)
		{
			cancellationToken.Register(state =>
			{
				var tuple = ((CacheEntryKeyLock Lock, string Key, TaskCompletionSource<ICacheEntry?> Tcs))state!;
				tuple.Tcs.TrySetCanceled(cancellationToken);
			}, (this, cacheKey, completionSource));
		}

		return completionSource.Task;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private bool TryRemoveCurrent(string cacheKey, ulong generation, out TaskCompletionSource<ICacheEntry?>? completionSource)
	{
		lock (keyLocks)
		{
			if (keyLocks.TryGetValue(cacheKey, out var entry) && entry.Generation == generation)
			{
				keyLocks.Remove(cacheKey);
				completionSource = entry.CompletionSource;
				return true;
			}

			completionSource = null;
			return false;
		}
	}

	public void ReleaseLock(string cacheKey, ulong generation, ICacheEntry? cacheEntry)
	{
		if (TryRemoveCurrent(cacheKey, generation, out var completionSource))
		{
			completionSource?.TrySetResult(cacheEntry);
		}
	}

	public void ReleaseLock(string cacheKey, ulong generation, Exception exception)
	{
		if (TryRemoveCurrent(cacheKey, generation, out var completionSource))
		{
			completionSource?.TrySetException(exception);
		}
	}

	/// <summary>
	/// Marks the key as evicted, releasing any waiters with <see langword="null"/> so they re-evaluate rather than observing a resurrected value.
	/// </summary>
	/// <remarks>
	/// If a refresh owner is still in flight, the generation is bumped and its pending completion is released with <see langword="null"/>.
	/// If no operation is in flight, the key entry is removed entirely.
	/// </remarks>
	public void NotifyEviction(string cacheKey)
	{
		TaskCompletionSource<ICacheEntry?>? completionSource;
		var hasWaiters = false;

		lock (keyLocks)
		{
			if (keyLocks.TryGetValue(cacheKey, out var entry))
			{
				completionSource = entry.CompletionSource;
				hasWaiters = completionSource != null;
				if (hasWaiters)
				{
					//A new unlocked entry replaces the completed waiters so released callers can re-enter immediately rather than busy loop.
					//An in-flight owner still holds the old generation and cannot publish against this entry.
					keyLocks[cacheKey] = new KeyLockEntry(entry.Generation + 1, null);
				}
				else
				{
					//Either an owner is in flight (it captured the old generation locally and can no longer publish) or the entry is stale - remove it.
					keyLocks.Remove(cacheKey);
				}
			}
		}

		if (hasWaiters)
		{
			completionSource!.TrySetResult(null);
		}
	}
}
