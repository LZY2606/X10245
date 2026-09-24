using System;
using System.Threading;
using System.Threading.Tasks;
using CacheTower.Providers.Memory;

namespace CacheTower.Tests.Utils;

/// <summary>
/// A controllable cache layer for tests. It wraps a real MemoryCacheLayer and adds
/// call counting, gates and failure injection. It implements the public ICacheLayer
/// contract, so the cache stack drives it exactly like any production layer.
/// </summary>
public sealed class ControllableCacheLayer : ICacheLayer
{
	private readonly MemoryCacheLayer Inner = new();

	private int getCount;
	private int setCount;
	private int evictCount;
	private int flushCount;

	public int GetCount => getCount;
	public int SetCount => setCount;
	public int EvictCount => evictCount;
	public int FlushCount => flushCount;

	/// <summary>
	/// Optional hook awaited before every GetAsync. Can be used to gate the call or to
	/// inject a failure (for example, throw a CacheSerializationException to simulate a corrupted layer).
	/// </summary>
	public Func<string, ValueTask>? BeforeGet { get; set; }

	/// <summary>
	/// Optional hook awaited before every SetAsync.
	/// </summary>
	public Func<string, ValueTask>? BeforeSet { get; set; }

	public ValueTask CleanupAsync() => Inner.CleanupAsync();

	public async ValueTask EvictAsync(string cacheKey)
	{
		Interlocked.Increment(ref evictCount);
		await Inner.EvictAsync(cacheKey);
	}

	public async ValueTask FlushAsync()
	{
		Interlocked.Increment(ref flushCount);
		await Inner.FlushAsync();
	}

	public async ValueTask<CacheEntry<T>?> GetAsync<T>(string cacheKey)
	{
		Interlocked.Increment(ref getCount);
		if (BeforeGet is not null)
		{
			await BeforeGet(cacheKey);
		}

		return await Inner.GetAsync<T>(cacheKey);
	}

	public ValueTask<bool> IsAvailableAsync(string cacheKey) => Inner.IsAvailableAsync(cacheKey);

	public async ValueTask SetAsync<T>(string cacheKey, CacheEntry<T> cacheEntry)
	{
		Interlocked.Increment(ref setCount);
		if (BeforeSet is not null)
		{
			await BeforeSet(cacheKey);
		}

		await Inner.SetAsync(cacheKey, cacheEntry);
	}
}
