using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using CacheTower.Internal;
using CacheTower.Serializers;

namespace CacheTower.Tests.Utils;

/// <summary>
/// A controllable <see cref="ICacheLayer"/> for tests. It is dictionary-backed and exposes toggles for
/// corruption (throwing <see cref="CacheSerializationException"/>), availability, call counting and
/// get-gating so tests can deterministically control timing.
/// The cache stack only ever interacts with it through the public <see cref="ICacheLayer"/> surface.
/// </summary>
public class ControllableCacheLayer : ILocalCacheLayer
{
	private readonly ConcurrentDictionary<string, ICacheEntry> Entries = new(StringComparer.Ordinal);

	private int getCallCount;
	private int setCallCount;
	private int evictCallCount;

	/// <summary>Whether the layer reports itself as available.</summary>
	public bool IsAvailable { get; set; } = true;
	/// <summary>When true, <see cref="GetAsync{T}"/> throws <see cref="CacheSerializationException"/> (simulates a corrupted layer).</summary>
	public bool CorruptGets { get; set; }
	/// <summary>When true, <see cref="SetAsync{T}"/> throws <see cref="CacheSerializationException"/> (simulates a corrupted layer).</summary>
	public bool CorruptSets { get; set; }
	/// <summary>Optional hook awaited inside <see cref="GetAsync{T}"/> before the lookup, used to gate timing.</summary>
	public Func<string, Task>? BeforeGetAsync { get; set; }

	public int GetCallCount => getCallCount;
	public int SetCallCount => setCallCount;
	public int EvictCallCount => evictCallCount;

	public ValueTask CleanupAsync()
	{
		var currentTime = DateTimeProvider.Now;
		foreach (var pair in Entries)
		{
			if (pair.Value.Expiry < currentTime)
			{
				Entries.TryRemove(pair.Key, out _);
			}
		}
		return new ValueTask();
	}

	public ValueTask EvictAsync(string cacheKey)
	{
		Interlocked.Increment(ref evictCallCount);
		Entries.TryRemove(cacheKey, out _);
		return new ValueTask();
	}

	public ValueTask FlushAsync()
	{
		Entries.Clear();
		return new ValueTask();
	}

	public async ValueTask<CacheEntry<T>?> GetAsync<T>(string cacheKey)
	{
		Interlocked.Increment(ref getCallCount);
		if (BeforeGetAsync is not null)
		{
			await BeforeGetAsync(cacheKey).ConfigureAwait(false);
		}
		if (CorruptGets)
		{
			throw new CacheSerializationException("ControllableCacheLayer: simulated corruption on get");
		}
		if (Entries.TryGetValue(cacheKey, out var cacheEntry))
		{
			return cacheEntry as CacheEntry<T>;
		}
		return default;
	}

	public ValueTask<bool> IsAvailableAsync(string cacheKey)
	{
		return new ValueTask<bool>(IsAvailable);
	}

	public ValueTask SetAsync<T>(string cacheKey, CacheEntry<T> cacheEntry)
	{
		Interlocked.Increment(ref setCallCount);
		if (CorruptSets)
		{
			throw new CacheSerializationException("ControllableCacheLayer: simulated corruption on set");
		}
		Entries[cacheKey] = cacheEntry;
		return new ValueTask();
	}
}
