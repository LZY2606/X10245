using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace CacheTower.Tests.Utils;

/// <summary>
/// An <see cref="ICacheLayer"/> test double with controllable behaviour: call counts, availability,
/// and hooks that can delay or fail (e.g. simulate a corrupted layer) individual operations.
/// Tests only ever drive it through the public <see cref="ICacheLayer"/> surface.
/// </summary>
public class ControllableCacheLayer : ICacheLayer
{
	private readonly ConcurrentDictionary<string, ICacheEntry> Cache = new(StringComparer.Ordinal);

	public int GetCallCount;
	public int SetCallCount;
	public int EvictCallCount;
	public bool IsAvailable = true;

	/// <summary>
	/// Hook invoked before every GetAsync. Can delay or throw (e.g. CacheSerializationException to simulate corruption).
	/// </summary>
	public Func<string, Task>? BeforeGetAsync;

	/// <summary>
	/// Hook invoked before every SetAsync.
	/// </summary>
	public Func<string, Task>? BeforeSetAsync;

	public ValueTask CleanupAsync()
	{
		return new ValueTask();
	}

	public ValueTask FlushAsync()
	{
		Cache.Clear();
		return new ValueTask();
	}

	public ValueTask EvictAsync(string cacheKey)
	{
		Interlocked.Increment(ref EvictCallCount);
		Cache.TryRemove(cacheKey, out _);
		return new ValueTask();
	}

	public async ValueTask<CacheEntry<T>?> GetAsync<T>(string cacheKey)
	{
		Interlocked.Increment(ref GetCallCount);
		if (BeforeGetAsync != null)
		{
			await BeforeGetAsync(cacheKey).ConfigureAwait(false);
		}
		return Cache.TryGetValue(cacheKey, out var cacheEntry) ? cacheEntry as CacheEntry<T> : default;
	}

	public ValueTask<bool> IsAvailableAsync(string cacheKey)
	{
		return new ValueTask<bool>(IsAvailable);
	}

	public async ValueTask SetAsync<T>(string cacheKey, CacheEntry<T> cacheEntry)
	{
		Interlocked.Increment(ref SetCallCount);
		if (BeforeSetAsync != null)
		{
			await BeforeSetAsync(cacheKey).ConfigureAwait(false);
		}
		Cache[cacheKey] = cacheEntry;
	}
}
