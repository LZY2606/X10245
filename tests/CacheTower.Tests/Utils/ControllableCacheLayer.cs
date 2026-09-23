using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using CacheTower.Serializers;

namespace CacheTower.Tests.Utils;

/// <summary>
/// A controllable <see cref="ICacheLayer"/> test double.
/// It only implements the public layer contract - tests still enter through the real <see cref="CacheStack"/> entry points.
/// Call counters and fault-injection switches allow tests to prove cache/single-flight behaviour and to simulate a corrupted layer.
/// </summary>
public class ControllableCacheLayer : ICacheLayer
{
	private readonly ConcurrentDictionary<string, ICacheEntry> Cache = new(StringComparer.Ordinal);

	/// <summary>
	/// When <see langword="false"/>, the layer reports itself as unavailable and is skipped by the cache stack.
	/// </summary>
	public bool IsAvailable { get; set; } = true;

	/// <summary>
	/// When <see langword="true"/>, reads throw a <see cref="CacheSerializationException"/>, simulating a corrupted layer.
	/// </summary>
	public bool ThrowSerializationExceptionOnGet { get; set; }

	private int getCount;
	private int setCount;
	private int evictCount;
	private int flushCount;
	private int cleanupCount;

	public int GetCount => getCount;
	public int SetCount => setCount;
	public int EvictCount => evictCount;
	public int FlushCount => flushCount;
	public int CleanupCount => cleanupCount;

	public ValueTask CleanupAsync()
	{
		Interlocked.Increment(ref cleanupCount);
		return new ValueTask();
	}

	public ValueTask EvictAsync(string cacheKey)
	{
		Interlocked.Increment(ref evictCount);
		Cache.TryRemove(cacheKey, out _);
		return new ValueTask();
	}

	public ValueTask FlushAsync()
	{
		Interlocked.Increment(ref flushCount);
		Cache.Clear();
		return new ValueTask();
	}

	public ValueTask<CacheEntry<T>?> GetAsync<T>(string cacheKey)
	{
		Interlocked.Increment(ref getCount);
		if (ThrowSerializationExceptionOnGet)
		{
			throw new CacheSerializationException($"Injected corruption while reading '{cacheKey}'");
		}

		return new ValueTask<CacheEntry<T>?>(Cache.TryGetValue(cacheKey, out var cacheEntry) ? cacheEntry as CacheEntry<T> : default);
	}

	public ValueTask<bool> IsAvailableAsync(string cacheKey)
	{
		return new ValueTask<bool>(IsAvailable);
	}

	public ValueTask SetAsync<T>(string cacheKey, CacheEntry<T> cacheEntry)
	{
		Interlocked.Increment(ref setCount);
		Cache[cacheKey] = cacheEntry;
		return new ValueTask();
	}
}
