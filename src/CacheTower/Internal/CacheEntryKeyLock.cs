using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace CacheTower.Internal;

/// <summary>
/// Co-ordinates single-flight refreshes per cache key and tracks the eviction generation of a key.
/// </summary>
/// <remarks>
/// The eviction generation increments on every <see cref="NotifyEvicted"/> call.
/// Refresh callers capture the generation when acquiring the lock and compare it before committing a refreshed entry -
/// if an eviction happened while the value factory was running, the refreshed entry is not written back,
/// preventing a delete/refresh race from resurrecting an evicted value.
/// </remarks>
internal sealed class CacheEntryKeyLock
{
	private sealed class KeyState
	{
		public long EvictionGeneration;
		public bool IsLocked;
		public TaskCompletionSource<ICacheEntry>? CompletionSource;
	}

	private readonly object SyncLock = new();
	private readonly Dictionary<string, KeyState> keyStates = new(StringComparer.Ordinal);

	public CacheEntryKeyLock() { }

	private KeyState GetOrCreateState(string cacheKey)
	{
		if (!keyStates.TryGetValue(cacheKey, out var state))
		{
			state = new KeyState();
			keyStates[cacheKey] = state;
		}
		return state;
	}

	/// <summary>
	/// Attempts to acquire the single-flight lock for <paramref name="cacheKey"/>.
	/// When successful, outputs the eviction generation observed at acquisition - compare it with
	/// <see cref="GetEvictionGeneration"/> before committing a refreshed value.
	/// </summary>
	public bool AcquireLock(string cacheKey, out long evictionGeneration)
	{
		lock (SyncLock)
		{
			var state = GetOrCreateState(cacheKey);
			if (state.IsLocked)
			{
				evictionGeneration = state.EvictionGeneration;
				return false;
			}

			state.IsLocked = true;
			state.CompletionSource = null;
			evictionGeneration = state.EvictionGeneration;
			return true;
		}
	}

	/// <summary>
	/// Gets the current eviction generation for <paramref name="cacheKey"/>.
	/// </summary>
	public long GetEvictionGeneration(string cacheKey)
	{
		lock (SyncLock)
		{
			return keyStates.TryGetValue(cacheKey, out var state) ? state.EvictionGeneration : 0;
		}
	}

	/// <summary>
	/// Records that <paramref name="cacheKey"/> was evicted, incrementing its eviction generation.
	/// Any in-flight refresh that observed an earlier generation must not write its result back.
	/// </summary>
	public void NotifyEvicted(string cacheKey)
	{
		lock (SyncLock)
		{
			var state = GetOrCreateState(cacheKey);
			state.EvictionGeneration++;
			if (!state.IsLocked && state.CompletionSource == null)
			{
				keyStates.Remove(cacheKey);
			}
		}
	}

	public Task<ICacheEntry> WaitAsync(string cacheKey)
	{
		lock (SyncLock)
		{
			var state = GetOrCreateState(cacheKey);
			if (state.CompletionSource == null)
			{
				state.CompletionSource = new TaskCompletionSource<ICacheEntry>(
#if NETSTANDARD2_1_OR_GREATER
					TaskCreationOptions.RunContinuationsAsynchronously
#endif
				);
			}
			return state.CompletionSource.Task;
		}
	}

	/// <summary>
	/// Releases the single-flight lock for <paramref name="cacheKey"/>, completing any waiting refreshers with <paramref name="cacheEntry"/>.
	/// The state is removed when the eviction generation hasn't changed since <paramref name="observedGeneration"/>;
	/// otherwise the bumped generation is retained so a subsequent eviction is still observable.
	/// </summary>
	public void ReleaseLock(string cacheKey, long observedGeneration, ICacheEntry? cacheEntry)
	{
		lock (SyncLock)
		{
			if (keyStates.TryGetValue(cacheKey, out var state))
			{
				var completionSource = state.CompletionSource;
				state.IsLocked = false;
				state.CompletionSource = null;
				if (state.EvictionGeneration == observedGeneration)
				{
					keyStates.Remove(cacheKey);
				}
				completionSource?.TrySetResult(cacheEntry!);
			}
		}
	}

	/// <summary>
	/// Releases the single-flight lock for <paramref name="cacheKey"/>, faulting any waiting refreshers with <paramref name="exception"/>.
	/// </summary>
	public void ReleaseLock(string cacheKey, long observedGeneration, Exception exception)
	{
		lock (SyncLock)
		{
			if (keyStates.TryGetValue(cacheKey, out var state))
			{
				var completionSource = state.CompletionSource;
				state.IsLocked = false;
				state.CompletionSource = null;
				if (state.EvictionGeneration == observedGeneration)
				{
					keyStates.Remove(cacheKey);
				}
				completionSource?.TrySetException(exception);
			}
		}
	}
}
