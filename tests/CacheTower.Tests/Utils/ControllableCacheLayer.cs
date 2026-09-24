using System;
using System.Threading;
using System.Threading.Tasks;
using CacheTower.Providers.Memory;
using CacheTower.Serializers;

namespace CacheTower.Tests.Utils
{
	/// <summary>
	/// A controllable cache layer for tests. It wraps MemoryCacheLayer and adds failure injection
	/// (corrupted reads/writes), availability toggling and call counting. It only ever interacts
	/// with the system under test through the public ICacheLayer contract.
	/// </summary>
	public sealed class ControllableCacheLayer : ILocalCacheLayer
	{
		private readonly MemoryCacheLayer Inner = new();

		private int getCallCount;
		private int setCallCount;
		private int evictCallCount;

		public int GetCallCount => getCallCount;
		public int SetCallCount => setCallCount;
		public int EvictCallCount => evictCallCount;

		public bool Unavailable { get; set; }
		public bool CorruptReads { get; set; }
		public bool CorruptWrites { get; set; }

		public Action<string> OnSet { get; set; }

		public ValueTask CleanupAsync() => Inner.CleanupAsync();

		public ValueTask EvictAsync(string cacheKey)
		{
			Interlocked.Increment(ref evictCallCount);
			return Inner.EvictAsync(cacheKey);
		}

		public ValueTask FlushAsync() => Inner.FlushAsync();

		public async ValueTask<CacheEntry<T>> GetAsync<T>(string cacheKey)
		{
			Interlocked.Increment(ref getCallCount);
			if (CorruptReads)
			{
				throw new CacheSerializationException("Simulated corrupted read for cache key: " + cacheKey);
			}

			return await Inner.GetAsync<T>(cacheKey).ConfigureAwait(false);
		}

		public ValueTask<bool> IsAvailableAsync(string cacheKey) => new(!Unavailable);

		public async ValueTask SetAsync<T>(string cacheKey, CacheEntry<T> cacheEntry)
		{
			Interlocked.Increment(ref setCallCount);
			if (CorruptWrites)
			{
				throw new CacheSerializationException("Simulated corrupted write for cache key: " + cacheKey);
			}

			OnSet?.Invoke(cacheKey);
			await Inner.SetAsync(cacheKey, cacheEntry).ConfigureAwait(false);
		}

		public Task<CacheEntry<T>> InspectAsync<T>(string cacheKey) => Inner.GetAsync<T>(cacheKey).AsTask();
	}
}
