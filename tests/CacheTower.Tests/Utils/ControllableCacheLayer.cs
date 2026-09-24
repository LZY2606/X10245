using System;
using System.Threading;
using System.Threading.Tasks;
using CacheTower.Providers.Memory;

namespace CacheTower.Tests.Utils;

/// <summary>
/// A controllable <see cref="ICacheLayer"/> used to script cache layer behaviour in tests:
/// gating reads, injecting corruption (as <see cref="CacheTower.Serializers.CacheSerializationException"/>) and counting calls.
/// Storage itself is delegated to a real <see cref="MemoryCacheLayer"/> so no caching logic is re-implemented for tests.
/// </summary>
public sealed class ControllableCacheLayer : ICacheLayer
{
	private readonly MemoryCacheLayer InnerLayer = new();
	private volatile Exception? getException;
	private volatile bool isAvailable = true;
	private volatile Task? getGate;

	private int getCallCount;
	private int setCallCount;
	private int evictCallCount;

	public int GetCallCount => getCallCount;
	public int SetCallCount => setCallCount;
	public int EvictCallCount => evictCallCount;

	public void SetAvailable(bool available) => isAvailable = available;

	public void ThrowOnGet(Exception? exception) => getException = exception;

	/// <summary>
	/// Blocks all reads until the given task completes; pass <see langword="null"/> to stop gating.
	/// </summary>
	public void GateReads(Task? gate) => getGate = gate;

	public ValueTask<bool> IsAvailableAsync(string cacheKey) => new(isAvailable);

	public async ValueTask<CacheEntry<T>?> GetAsync<T>(string cacheKey)
	{
		Interlocked.Increment(ref getCallCount);

		var gate = getGate;
		if (gate is not null)
		{
			await gate.ConfigureAwait(false);
		}

		var exception = getException;
		if (exception is not null)
		{
			throw exception;
		}

		return await InnerLayer.GetAsync<T>(cacheKey).ConfigureAwait(false);
	}

	public ValueTask SetAsync<T>(string cacheKey, CacheEntry<T> cacheEntry)
	{
		Interlocked.Increment(ref setCallCount);
		return InnerLayer.SetAsync(cacheKey, cacheEntry);
	}

	public ValueTask EvictAsync(string cacheKey)
	{
		Interlocked.Increment(ref evictCallCount);
		return InnerLayer.EvictAsync(cacheKey);
	}

	public ValueTask FlushAsync() => InnerLayer.FlushAsync();

	public ValueTask CleanupAsync() => InnerLayer.CleanupAsync();
}
