using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CacheTower.Serializers;

namespace CacheTower.Tests.Utils;

/// <summary>
/// A controllable <see cref="ICacheLayer"/> test double. It behaves as a backing (non-local) layer,
/// supports gating individual operations, fault injection and call counting so tests can
/// deterministically steer the cache stack through races and failures.
/// </summary>
public sealed class ControllableCacheLayer : ICacheLayer
{
	private readonly ConcurrentDictionary<string, ICacheEntry> Store = new(StringComparer.Ordinal);

	public int GetCount;
	public int SetCount;
	public int EvictCount;

	public bool IsAvailable { get; set; } = true;

	/// <summary>
	/// When set, <see cref="GetAsync{T}"/> throws this exception (simulating a corrupted backing layer).
	/// </summary>
	public Exception GetFault { get; set; }

	/// <summary>
	/// When set, <see cref="GetAsync{T}"/> waits for this gate before reading.
	/// </summary>
	public TaskCompletionSource<bool> GetGate { get; set; }

	/// <summary>
	/// When set, <see cref="SetAsync{T}"/> waits for this gate before writing.
	/// </summary>
	public TaskCompletionSource<bool> SetGate { get; set; }

	/// <summary>
	/// When set, <see cref="EvictAsync"/> waits for this gate before evicting.
	/// </summary>
	public TaskCompletionSource<bool> EvictGate { get; set; }

	public ValueTask FlushAsync()
	{
		Store.Clear();
		return new ValueTask();
	}

	public ValueTask CleanupAsync()
	{
		return new ValueTask();
	}

	public async ValueTask EvictAsync(string cacheKey)
	{
		Interlocked.Increment(ref EvictCount);
		if (EvictGate is { } gate)
		{
			await gate.Task.ConfigureAwait(false);
		}

		Store.TryRemove(cacheKey, out _);
	}

	public async ValueTask<CacheEntry<T>> GetAsync<T>(string cacheKey)
	{
		Interlocked.Increment(ref GetCount);
		if (GetGate is { } gate)
		{
			await gate.Task.ConfigureAwait(false);
		}

		if (GetFault is { } fault)
		{
			throw fault;
		}

		if (Store.TryGetValue(cacheKey, out var cacheEntry))
		{
			return cacheEntry as CacheEntry<T>;
		}

		return default;
	}

	public async ValueTask SetAsync<T>(string cacheKey, CacheEntry<T> cacheEntry)
	{
		Interlocked.Increment(ref SetCount);
		if (SetGate is { } gate)
		{
			await gate.Task.ConfigureAwait(false);
		}

		Store[cacheKey] = cacheEntry;
	}

	public ValueTask<bool> IsAvailableAsync(string cacheKey)
	{
		return new ValueTask<bool>(IsAvailable);
	}
}
