using System;
using System.Threading;
using System.Threading.Tasks;
using CacheTower.Providers.Memory;

namespace CacheTower.Tests.Utils;

/// <summary>
/// A controllable cache layer for tests. It delegates storage to a real <see cref="MemoryCacheLayer"/>
/// while allowing failure injection, call counting and gated (blockable) operations, so tests can
/// drive the public cache stack through deterministic cache-layer behaviour.
/// </summary>
internal sealed class ControllableCacheLayer : ICacheLayer
{
	private readonly MemoryCacheLayer Inner = new();

	public ControllableCacheLayer(string name)
	{
		Name = name;
	}

	public string Name { get; }

	public int GetCallCount;
	public int SetCallCount;

	/// <summary>When set, <see cref="GetAsync{T}"/> throws this exception (simulating a corrupted layer).</summary>
	public Exception GetFailure;

	/// <summary>Optional async gate invoked before every <see cref="GetAsync{T}"/>.</summary>
	public Func<string, Task> BeforeGet;

	/// <summary>Optional async gate invoked before every <see cref="SetAsync{T}"/>.</summary>
	public Func<string, Task> BeforeSet;

	/// <summary>Reads the inner layer directly, bypassing gates, failures and counters.</summary>
	public Task<CacheEntry<T>> PeekAsync<T>(string cacheKey) => Inner.GetAsync<T>(cacheKey).AsTask();

	public ValueTask CleanupAsync() => Inner.CleanupAsync();

	public ValueTask EvictAsync(string cacheKey) => Inner.EvictAsync(cacheKey);

	public ValueTask FlushAsync() => Inner.FlushAsync();

	public async ValueTask<CacheEntry<T>> GetAsync<T>(string cacheKey)
	{
		Interlocked.Increment(ref GetCallCount);
		if (BeforeGet != null)
		{
			await BeforeGet(cacheKey).ConfigureAwait(false);
		}
		if (GetFailure != null)
		{
			throw GetFailure;
		}
		return await Inner.GetAsync<T>(cacheKey).ConfigureAwait(false);
	}

	public ValueTask<bool> IsAvailableAsync(string cacheKey) => new(true);

	public async ValueTask SetAsync<T>(string cacheKey, CacheEntry<T> cacheEntry)
	{
		Interlocked.Increment(ref SetCallCount);
		if (BeforeSet != null)
		{
			await BeforeSet(cacheKey).ConfigureAwait(false);
		}
		await Inner.SetAsync(cacheKey, cacheEntry).ConfigureAwait(false);
	}
}
