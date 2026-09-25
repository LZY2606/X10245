using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using CacheTower.Serializers;

namespace CacheTower.Tests.Utils;

/// <summary>
/// A controllable <see cref="ICacheLayer"/> test double. It stores entries in memory like
/// the memory cache layer but lets a test toggle availability, simulate a corrupted layer
/// (via <see cref="CacheSerializationException"/>) and count every operation so tests can
/// assert internal invariants (single-flight, back-population, eviction).
/// </summary>
internal sealed class ControllableCacheLayer : ICacheLayer
{
	private readonly ConcurrentDictionary<string, ICacheEntry> Store = new(StringComparer.Ordinal);

	private int getCount;
	private int setCount;
	private int evictCount;
	private int flushCount;

	public ControllableCacheLayer(string name)
	{
		Name = name;
	}

	/// <summary>A stable name used in assertion messages to identify the layer.</summary>
	public string Name { get; }

	/// <summary>Controls what <see cref="IsAvailableAsync"/> reports.</summary>
	public bool IsAvailable { get; set; } = true;

	/// <summary>When set, <see cref="GetAsync{T}"/> throws a <see cref="CacheSerializationException"/>, simulating a corrupted layer.</summary>
	public bool ThrowSerializationExceptionOnGet { get; set; }

	public int GetCount => getCount;
	public int SetCount => setCount;
	public int EvictCount => evictCount;
	public int FlushCount => flushCount;

	public ValueTask<bool> IsAvailableAsync(string cacheKey)
	{
		return new ValueTask<bool>(IsAvailable);
	}

	public ValueTask<CacheEntry<T>?> GetAsync<T>(string cacheKey)
	{
		Interlocked.Increment(ref getCount);
		if (ThrowSerializationExceptionOnGet)
		{
			throw new CacheSerializationException($"Simulated corruption in layer '{Name}' for key '{cacheKey}'.");
		}

		return new ValueTask<CacheEntry<T>?>(Store.TryGetValue(cacheKey, out var cacheEntry) ? cacheEntry as CacheEntry<T> : null);
	}

	public ValueTask SetAsync<T>(string cacheKey, CacheEntry<T> cacheEntry)
	{
		Interlocked.Increment(ref setCount);
		Store[cacheKey] = cacheEntry;
		return new ValueTask();
	}

	/// <summary>Synchronously peeks at the stored entry for assertions; does not count as a <see cref="GetAsync{T}"/> call.</summary>
	public CacheEntry<T>? Peek<T>(string cacheKey)
	{
		return Store.TryGetValue(cacheKey, out var cacheEntry) ? cacheEntry as CacheEntry<T> : null;
	}

	public ValueTask EvictAsync(string cacheKey)
	{
		Interlocked.Increment(ref evictCount);
		Store.TryRemove(cacheKey, out _);
		return new ValueTask();
	}

	public ValueTask FlushAsync()
	{
		Interlocked.Increment(ref flushCount);
		Store.Clear();
		return new ValueTask();
	}

	public ValueTask CleanupAsync()
	{
		return new ValueTask();
	}
}
