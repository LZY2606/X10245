using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CacheTower.Internal;
using CacheTower.Serializers;

namespace CacheTower.Tests.StaleWhileRevalidate;

/// <summary>
/// A minimal, fully controllable <see cref="ILocalCacheLayer"/> used to drive the real <see cref="CacheStack"/>
/// through its public entry points. It records every operation and can simulate a corrupt backing layer
/// or a change in completion scheduling - it never impersonates the cache stack's internal objects.
/// </summary>
internal sealed class ControllableCacheLayer : ILocalCacheLayer
{
	private readonly Dictionary<string, ICacheEntry> entries = new(StringComparer.Ordinal);
	private int getCount;
	private int setCount;
	private int evictCount;
	private int availabilityCheckCount;

	public int GetCount => Volatile.Read(ref getCount);
	public int SetCount => Volatile.Read(ref setCount);
	public int EvictCount => Volatile.Read(ref evictCount);
	public int AvailabilityCheckCount => Volatile.Read(ref availabilityCheckCount);

	/// <summary>Controls <see cref="IsAvailableAsync"/> - a backing layer can be present but refuse storage.</summary>
	public bool IsAvailable { get; set; } = true;

	/// <summary>When set, <see cref="GetAsync{T}"/> throws this (e.g. <see cref="CacheSerializationException"/> for a corrupt backing layer).</summary>
	public Exception? GetException { get; set; }

	/// <summary>When <see langword="true"/>, reads yield once before completing - a result-irrelevant scheduling variant.</summary>
	public bool YieldOnGet { get; set; }

	/// <summary>Invoked after a value is stored, allowing tests to interleave an eviction with a refresh completion.</summary>
	public Action<string>? AfterSet { get; set; }

	public ValueTask CleanupAsync()
	{
		var currentTime = DateTimeProvider.Now;
		lock (entries)
		{
			var expiredKeys = new List<string>();
			foreach (var pair in entries)
			{
				if (pair.Value.Expiry < currentTime)
				{
					expiredKeys.Add(pair.Key);
				}
			}
			foreach (var key in expiredKeys)
			{
				entries.Remove(key);
			}
		}
		return default;
	}

	public ValueTask EvictAsync(string cacheKey)
	{
		Interlocked.Increment(ref evictCount);
		lock (entries)
		{
			entries.Remove(cacheKey);
		}
		return default;
	}

	public ValueTask FlushAsync()
	{
		lock (entries)
		{
			entries.Clear();
		}
		return default;
	}

	public async ValueTask<CacheEntry<T>?> GetAsync<T>(string cacheKey)
	{
		Interlocked.Increment(ref getCount);
		if (YieldOnGet)
		{
			await Task.Yield();
		}

		if (GetException is not null)
		{
			throw GetException;
		}

		lock (entries)
		{
			return entries.TryGetValue(cacheKey, out var entry) ? entry as CacheEntry<T> : null;
		}
	}

	public ValueTask SetAsync<T>(string cacheKey, CacheEntry<T> cacheEntry)
	{
		Interlocked.Increment(ref setCount);
		lock (entries)
		{
			entries[cacheKey] = cacheEntry;
		}
		AfterSet?.Invoke(cacheKey);
		return default;
	}

	public ValueTask<bool> IsAvailableAsync(string cacheKey)
	{
		Interlocked.Increment(ref availabilityCheckCount);
		return new ValueTask<bool>(IsAvailable);
	}
}
