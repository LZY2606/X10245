using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using CacheTower.Serializers;

namespace CacheTower.Tests.StaleWhileRevalidate
{
	/// <summary>
	/// A controllable <see cref="ICacheLayer"/> test double. It implements the public cache layer
	/// abstraction so tests enter the cache stack through its formal public entry points; no internal
	/// final objects are forged.
	/// </summary>
	public sealed class ControllableCacheLayer : ILocalCacheLayer
	{
		private readonly ConcurrentDictionary<string, ICacheEntry> Cache = new(StringComparer.Ordinal);

		private int getCallCount;
		private int setCallCount;
		private int evictCallCount;

		public int GetCallCount => getCallCount;
		public int SetCallCount => setCallCount;
		public int EvictCallCount => evictCallCount;

		/// <summary>
		/// Controls the availability reported by <see cref="IsAvailableAsync"/>.
		/// </summary>
		public bool IsAvailable { get; set; } = true;

		/// <summary>
		/// When <see langword="true"/>, GetAsync throws a <see cref="CacheSerializationException"/>,
		/// simulating a corrupted backing layer. The failure is recoverable by setting this back to <see langword="false"/>.
		/// </summary>
		public bool CorruptGets { get; set; }

		/// <summary>
		/// An optional gate: when set, SetAsync waits for this task before storing the entry.
		/// </summary>
		public Task SetGate { get; set; }

		/// <summary>
		/// An optional callback invoked after each SetAsync has stored the entry.
		/// </summary>
		public Action<string> AfterSet { get; set; }

		public ValueTask CleanupAsync() => new();

		public ValueTask FlushAsync()
		{
			Cache.Clear();
			return new();
		}

		public ValueTask EvictAsync(string cacheKey)
		{
			Interlocked.Increment(ref evictCallCount);
			Cache.TryRemove(cacheKey, out _);
			return new();
		}

		public ValueTask<CacheEntry<T>> GetAsync<T>(string cacheKey)
		{
			Interlocked.Increment(ref getCallCount);
			if (CorruptGets)
			{
				throw new CacheSerializationException($"Simulated corruption while reading '{cacheKey}'.");
			}

			if (Cache.TryGetValue(cacheKey, out var cacheEntry))
			{
				return new ValueTask<CacheEntry<T>>(cacheEntry as CacheEntry<T>);
			}

			return new ValueTask<CacheEntry<T>>(default(CacheEntry<T>));
		}

		public ValueTask<bool> IsAvailableAsync(string cacheKey) => new(IsAvailable);

		public async ValueTask SetAsync<T>(string cacheKey, CacheEntry<T> cacheEntry)
		{
			Interlocked.Increment(ref setCallCount);
			var gate = SetGate;
			if (gate is not null)
			{
				await gate.ConfigureAwait(false);
			}

			Cache[cacheKey] = cacheEntry;
			AfterSet?.Invoke(cacheKey);
		}
	}
}
