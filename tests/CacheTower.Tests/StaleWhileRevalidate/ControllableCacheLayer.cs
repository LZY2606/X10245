using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CacheTower.Tests.StaleWhileRevalidate
{
	/// <summary>
	/// A deterministic, controllable <see cref="ICacheLayer"/> for the stale-while-revalidate regression materials.
	/// <para>
	/// Every call is appended to a thread-safe event log in arrival order so tests can assert not just the
	/// returned value but which layer served it, when sets/evictions happened and how the layers coordinated.
	/// </para>
	/// <para>
	/// The layer can be configured to fail <see cref="GetAsync{T}"/> with a <see cref="CacheSerializationException"/>
	/// to simulate a corrupted backup layer - the same exception type the production stack already skips layers for.
	/// </para>
	/// </summary>
	internal sealed class ControllableCacheLayer : ICacheLayer
	{
		public enum EventType
		{
			IsAvailable,
			Get,
			Set,
			Evict,
			Cleanup,
			Flush
		}

		public readonly record struct LayerEvent(EventType Type, string Key, int LayerIndex);

		private readonly int layerIndex;
		private readonly object syncLock = new();
		private readonly Dictionary<string, ICacheEntry> entries = new(StringComparer.Ordinal);
		private readonly List<LayerEvent> eventLog = new();

		public ControllableCacheLayer(int layerIndex)
		{
			this.layerIndex = layerIndex;
		}

		/// <summary>
		/// When set, <see cref="GetAsync{T}"/> for the configured key (or any key when null) throws this exception.
		/// </summary>
		public Exception? GetFailure { get; set; }

		/// <summary>
		/// Only applies <see cref="GetFailure"/> for this key. Null means the failure applies to every key.
		/// </summary>
		public string? GetFailureKey { get; set; }

		/// <summary>
		/// Signalled after every <see cref="GetAsync{T}"/> call completes (including failures).
		/// </summary>
		public event Action<string>? OnGet;

		/// <summary>
		/// Signalled after every <see cref="SetAsync{T}"/> call completes.
		/// </summary>
		public event Action<string>? OnSet;

		public IReadOnlyList<LayerEvent> SnapshotEvents()
		{
			lock (syncLock)
			{
				return eventLog.ToArray();
			}
		}

		public CacheEntry<T>? PeekEntry<T>(string cacheKey)
		{
			lock (syncLock)
			{
				return entries.TryGetValue(cacheKey, out var entry) ? entry as CacheEntry<T> : null;
			}
		}

		public void Seed<T>(string cacheKey, CacheEntry<T> cacheEntry)
		{
			lock (syncLock)
			{
				entries[cacheKey] = cacheEntry;
			}
		}

		public ValueTask FlushAsync()
		{
			lock (syncLock)
			{
				entries.Clear();
				eventLog.Add(new(EventType.Flush, string.Empty, layerIndex));
			}
			return default;
		}

		public ValueTask CleanupAsync()
		{
			lock (syncLock)
			{
				eventLog.Add(new(EventType.Cleanup, string.Empty, layerIndex));
			}
			return default;
		}

		public ValueTask EvictAsync(string cacheKey)
		{
			lock (syncLock)
			{
				entries.Remove(cacheKey);
				eventLog.Add(new(EventType.Evict, cacheKey, layerIndex));
			}
			return default;
		}

		public ValueTask<CacheEntry<T>?> GetAsync<T>(string cacheKey)
		{
			try
			{
				lock (syncLock)
				{
					eventLog.Add(new(EventType.Get, cacheKey, layerIndex));
					if (GetFailure is not null && (GetFailureKey is null || GetFailureKey == cacheKey))
					{
						throw GetFailure;
					}

					if (entries.TryGetValue(cacheKey, out var entry))
					{
						return new ValueTask<CacheEntry<T>?>(entry as CacheEntry<T>);
					}
				}
				return new ValueTask<CacheEntry<T>?>(default(CacheEntry<T>));
			}
			finally
			{
				OnGet?.Invoke(cacheKey);
			}
		}

		public ValueTask SetAsync<T>(string cacheKey, CacheEntry<T> cacheEntry)
		{
			lock (syncLock)
			{
				entries[cacheKey] = cacheEntry;
				eventLog.Add(new(EventType.Set, cacheKey, layerIndex));
			}
			OnSet?.Invoke(cacheKey);
			return default;
		}

		public ValueTask<bool> IsAvailableAsync(string cacheKey)
		{
			lock (syncLock)
			{
				eventLog.Add(new(EventType.IsAvailable, cacheKey, layerIndex));
			}
			return new ValueTask<bool>(true);
		}
	}
}
