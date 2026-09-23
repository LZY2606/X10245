using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CacheTower.Serializers;

namespace CacheTower.Tests.StaleWhileRevalidate
{
	/// <summary>
	/// A scriptable <see cref="ICacheLayer"/> used only by the stale-while-revalidate regression material.
	/// It is driven entirely through the public <see cref="ICacheLayer"/> surface - it never fabricates
	/// internal cache stack objects. Every call is recorded in stable order so receipts, back-population
	/// and single-flight behaviour can be asserted against a reproducible trace.
	/// </summary>
	public sealed class ControllableCacheLayer : ICacheLayer
	{
		public sealed record Operation(string Name, string Key);

		private readonly object syncLock = new();
		private readonly Dictionary<string, ICacheEntry> entries = new(StringComparer.Ordinal);
		private readonly List<Operation> operations = new();

		public string Name { get; }
		public int SetCount { get; private set; }
		public int GetCount { get; private set; }
		public int EvictCount { get; private set; }
		public int IsAvailableCount { get; private set; }

		/// <summary>
		/// When true, <see cref="GetAsync{T}"/> throws <see cref="CacheSerializationException"/> to emulate a
		/// corrupt backing layer. The stack is expected to skip this layer using its existing error layering.
		/// </summary>
		public bool ThrowOnGet { get; set; }

		/// <summary>
		/// Optional gate released by the test to suspend <see cref="GetAsync{T}"/>, enabling precise scheduling.
		/// </summary>
		public Func<string, Task>? GetGate { get; set; }

		public ControllableCacheLayer(string name)
		{
			Name = name;
		}

		public IReadOnlyList<Operation> SnapshotOperations()
		{
			lock (syncLock)
			{
				return operations.ToArray();
			}
		}

		public void Seed<T>(string cacheKey, CacheEntry<T> cacheEntry)
		{
			lock (syncLock)
			{
				entries[cacheKey] = cacheEntry;
			}
		}

		public bool ContainsEntry(string cacheKey)
		{
			lock (syncLock)
			{
				return entries.ContainsKey(cacheKey);
			}
		}

		public CacheEntry<T>? Peek<T>(string cacheKey)
		{
			lock (syncLock)
			{
				return entries.TryGetValue(cacheKey, out var entry) ? entry as CacheEntry<T> : null;
			}
		}

		public ValueTask FlushAsync()
		{
			lock (syncLock)
			{
				entries.Clear();
				operations.Add(new Operation(nameof(FlushAsync), string.Empty));
			}
			return default;
		}

		public ValueTask CleanupAsync()
		{
			lock (syncLock)
			{
				operations.Add(new Operation(nameof(CleanupAsync), string.Empty));
			}
			return default;
		}

		public ValueTask EvictAsync(string cacheKey)
		{
			lock (syncLock)
			{
				entries.Remove(cacheKey);
				EvictCount++;
				operations.Add(new Operation(nameof(EvictAsync), cacheKey));
			}
			return default;
		}

		public async ValueTask<CacheEntry<T>?> GetAsync<T>(string cacheKey)
		{
			if (GetGate != null)
			{
				await GetGate(cacheKey).ConfigureAwait(false);
			}

			lock (syncLock)
			{
				GetCount++;
				operations.Add(new Operation(nameof(GetAsync), cacheKey));
				if (ThrowOnGet)
				{
					throw new CacheSerializationException($"{Name} is corrupt for key {cacheKey}");
				}

				return entries.TryGetValue(cacheKey, out var entry) ? entry as CacheEntry<T> : null;
			}
		}

		public ValueTask SetAsync<T>(string cacheKey, CacheEntry<T> cacheEntry)
		{
			lock (syncLock)
			{
				SetCount++;
				entries[cacheKey] = cacheEntry;
				operations.Add(new Operation(nameof(SetAsync), cacheKey));
			}
			return default;
		}

		public ValueTask<bool> IsAvailableAsync(string cacheKey)
		{
			lock (syncLock)
			{
				IsAvailableCount++;
				operations.Add(new Operation(nameof(IsAvailableAsync), cacheKey));
			}
			return new ValueTask<bool>(true);
		}
	}
}
