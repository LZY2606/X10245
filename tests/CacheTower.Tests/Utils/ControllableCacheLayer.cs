#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using CacheTower.Providers.Memory;
using CacheTower.Serializers;

namespace CacheTower.Tests.Utils
{
	/// <summary>
	/// A controllable <see cref="ICacheLayer"/> used to deterministically exercise cache stack behaviour.
	/// It wraps a real <see cref="MemoryCacheLayer"/> and adds call counting, gates and failure injection.
	/// </summary>
	public sealed class ControllableCacheLayer : ICacheLayer
	{
		private readonly MemoryCacheLayer Inner = new();

		private int getCount;
		private int setCount;
		private int evictCount;

		public int GetCount => getCount;
		public int SetCount => setCount;
		public int EvictCount => evictCount;

		/// <summary>
		/// When enabled, <see cref="GetAsync{T}"/> throws a <see cref="CacheSerializationException"/> to simulate a corrupted layer.
		/// </summary>
		public bool IsCorrupted { get; set; }

		/// <summary>
		/// Optional hook invoked (and awaited) at the start of <see cref="GetAsync{T}"/>. Use to gate or delay reads.
		/// </summary>
		public Func<string, Task>? OnGetAsync { get; set; }

		/// <summary>
		/// Optional hook invoked (and awaited) at the start of <see cref="SetAsync{T}"/>. Use to gate or delay writes.
		/// </summary>
		public Func<string, Task>? OnSetAsync { get; set; }

		/// <summary>
		/// Optional hook invoked (and awaited) at the start of <see cref="EvictAsync"/>. Use to gate or delay evictions.
		/// </summary>
		public Func<string, Task>? OnEvictAsync { get; set; }

		public async ValueTask<CacheEntry<T>?> GetAsync<T>(string cacheKey)
		{
			Interlocked.Increment(ref getCount);
			if (OnGetAsync != null)
			{
				await OnGetAsync(cacheKey).ConfigureAwait(false);
			}

			if (IsCorrupted)
			{
				throw new CacheSerializationException($"Layer is corrupted and cannot serve CacheKey={cacheKey}.");
			}

			return await Inner.GetAsync<T>(cacheKey).ConfigureAwait(false);
		}

		public async ValueTask SetAsync<T>(string cacheKey, CacheEntry<T> cacheEntry)
		{
			Interlocked.Increment(ref setCount);
			if (OnSetAsync != null)
			{
				await OnSetAsync(cacheKey).ConfigureAwait(false);
			}

			await Inner.SetAsync(cacheKey, cacheEntry).ConfigureAwait(false);
		}

		public async ValueTask EvictAsync(string cacheKey)
		{
			Interlocked.Increment(ref evictCount);
			if (OnEvictAsync != null)
			{
				await OnEvictAsync(cacheKey).ConfigureAwait(false);
			}

			await Inner.EvictAsync(cacheKey).ConfigureAwait(false);
		}

		public ValueTask<bool> IsAvailableAsync(string cacheKey) => new(true);

		public ValueTask FlushAsync() => Inner.FlushAsync();

		public ValueTask CleanupAsync() => Inner.CleanupAsync();
	}
}
