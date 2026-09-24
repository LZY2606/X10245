using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using CacheTower.Serializers;

namespace CacheTower.Tests.Utils
{
	/// <summary>
	/// A controllable <see cref="ICacheLayer"/> for tests. It deliberately does not implement <see cref="ILocalCacheLayer"/>,
	/// so a <see cref="CacheStack"/> treats it as a fallback (backing) layer for source receipts.
	/// Storage semantics mirror <see cref="CacheTower.Providers.Memory.MemoryCacheLayer"/> while allowing tests to
	/// simulate a corrupted layer, toggle availability and count operations.
	/// </summary>
	public sealed class ControllableCacheLayer : ICacheLayer
	{
		private readonly ConcurrentDictionary<string, ICacheEntry> cache = new(StringComparer.Ordinal);

		private int getCount;
		private int setCount;
		private int evictCount;

		public int GetCount => getCount;
		public int SetCount => setCount;
		public int EvictCount => evictCount;

		/// <summary>
		/// When enabled, <see cref="GetAsync{T}"/> throws a <see cref="CacheSerializationException"/>, simulating a corrupted backing layer.
		/// </summary>
		public bool SimulateCorruption { get; set; }

		/// <summary>
		/// Controls the result of <see cref="IsAvailableAsync"/>.
		/// </summary>
		public bool IsAvailable { get; set; } = true;

		public ValueTask CleanupAsync()
		{
			foreach (var cachePair in cache)
			{
				if (cachePair.Value.Expiry < Internal.DateTimeProvider.Now)
				{
					cache.TryRemove(cachePair.Key, out _);
				}
			}

			return new ValueTask();
		}

		public ValueTask EvictAsync(string cacheKey)
		{
			Interlocked.Increment(ref evictCount);
			cache.TryRemove(cacheKey, out _);
			return new ValueTask();
		}

		public ValueTask FlushAsync()
		{
			cache.Clear();
			return new ValueTask();
		}

		public ValueTask<CacheEntry<T>?> GetAsync<T>(string cacheKey)
		{
			Interlocked.Increment(ref getCount);
			if (SimulateCorruption)
			{
				throw new CacheSerializationException($"Simulated corruption of the backing layer while reading '{cacheKey}'.");
			}

			if (cache.TryGetValue(cacheKey, out var cacheEntry))
			{
				return new ValueTask<CacheEntry<T>?>(cacheEntry as CacheEntry<T>);
			}

			return new ValueTask<CacheEntry<T>?>(default(CacheEntry<T>));
		}

		public ValueTask<bool> IsAvailableAsync(string cacheKey)
		{
			return new ValueTask<bool>(IsAvailable);
		}

		public ValueTask SetAsync<T>(string cacheKey, CacheEntry<T> cacheEntry)
		{
			Interlocked.Increment(ref setCount);
			cache[cacheKey] = cacheEntry;
			return new ValueTask();
		}
	}
}
