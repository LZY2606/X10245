using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using CacheTower.Serializers;

namespace CacheTower.Tests.Utils;

/// <summary>
/// A controllable <see cref="ICacheLayer"/> for tests. It stores entries in memory like
/// <see cref="CacheTower.Providers.Memory.MemoryCacheLayer"/> but allows tests to gate
/// individual operations, simulate corruption and count calls.
/// Tests only ever interact with a <see cref="CacheStack"/> through the public entry points;
/// this layer is plugged in through the public <see cref="ICacheLayer"/> abstraction.
/// </summary>
internal class ControllableCacheLayer : ILocalCacheLayer
{
	private readonly ConcurrentDictionary<string, ICacheEntry> Cache = new(StringComparer.Ordinal);

	private int getCount;
	private int setCount;
	private int evictCount;

	public int GetCount => getCount;
	public int SetCount => setCount;
	public int EvictCount => evictCount;

	/// <summary>When false, the layer reports itself as unavailable.</summary>
	public bool IsAvailable { get; set; } = true;

	/// <summary>When true, reads fail with a <see cref="CacheSerializationException"/> to simulate a corrupted layer.</summary>
	public bool SimulateCorruptionOnGet { get; set; }

	/// <summary>Optional gate: when set, <see cref="GetAsync{T}"/> waits for this task before reading.</summary>
	public Task? GetGate { get; set; }

	/// <summary>Optional gate: when set, <see cref="SetAsync{T}"/> waits for this task before writing.</summary>
	public Task? SetGate { get; set; }

	public ValueTask CleanupAsync() => new();

	public ValueTask FlushAsync()
	{
		Cache.Clear();
		return new();
	}

	public ValueTask EvictAsync(string cacheKey)
	{
		Interlocked.Increment(ref evictCount);
		Cache.TryRemove(cacheKey, out _);
		return new();
	}

	public async ValueTask<CacheEntry<T>?> GetAsync<T>(string cacheKey)
	{
		Interlocked.Increment(ref getCount);
		if (GetGate is not null)
		{
			await GetGate.ConfigureAwait(false);
		}

		if (SimulateCorruptionOnGet)
		{
			throw new CacheSerializationException("Simulated corruption of cache layer");
		}

		if (Cache.TryGetValue(cacheKey, out var cacheEntry))
		{
			return cacheEntry as CacheEntry<T>;
		}

		return default;
	}

	public async ValueTask SetAsync<T>(string cacheKey, CacheEntry<T> cacheEntry)
	{
		Interlocked.Increment(ref setCount);
		if (SetGate is not null)
		{
			await SetGate.ConfigureAwait(false);
		}

		Cache[cacheKey] = cacheEntry;
	}

	public ValueTask<bool> IsAvailableAsync(string cacheKey) => new(IsAvailable);
}
