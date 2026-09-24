using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CacheTower.Tests.Utils;

/// <summary>
/// An <see cref="ICacheLayer"/> test double with call counting, an ordered call log,
/// controllable availability and injectable read failures (eg. a corrupted layer).
/// Storage semantics mirror the real memory layer: entries are stored by reference.
/// </summary>
internal sealed class ControllableCacheLayer : ICacheLayer
{
	private readonly ConcurrentDictionary<string, ICacheEntry> Entries = new(StringComparer.Ordinal);
	private readonly ConcurrentQueue<string> Calls = new();
	private int getCount;
	private int setCount;
	private int evictCount;
	private int flushCount;
	private int cleanupCount;
	private int availabilityCount;
	private volatile bool available = true;
	private volatile Exception? getFailure;

	public ControllableCacheLayer(string name)
	{
		Name = name;
	}

	public string Name { get; }

	public int GetCount => getCount;
	public int SetCount => setCount;
	public int EvictCount => evictCount;
	public int FlushCount => flushCount;
	public int CleanupCount => cleanupCount;
	public int AvailabilityCount => availabilityCount;

	/// <summary>
	/// The ordered log of layer operations, eg. "Get:my-key". Used to assert stable operation ordering.
	/// </summary>
	public IReadOnlyList<string> CallLog => Calls.ToArray();

	/// <summary>
	/// Controls the result of <see cref="IsAvailableAsync(string)"/>.
	/// </summary>
	public bool IsAvailable
	{
		get => available;
		set => available = value;
	}

	/// <summary>
	/// Makes all subsequent <see cref="GetAsync{T}(string)"/> calls throw the given exception,
	/// simulating a corrupted layer. Pass <see langword="null"/> to heal the layer.
	/// </summary>
	public void FailGetsWith(Exception? exception) => getFailure = exception;

	private void Record(string operation, string cacheKey)
	{
		Calls.Enqueue($"{Name}.{operation}:{cacheKey}");
	}

	public ValueTask<bool> IsAvailableAsync(string cacheKey)
	{
		Record(nameof(IsAvailableAsync), cacheKey);
		Interlocked.Increment(ref availabilityCount);
		return new(available);
	}

	public ValueTask<CacheEntry<T>?> GetAsync<T>(string cacheKey)
	{
		Record(nameof(GetAsync), cacheKey);
		Interlocked.Increment(ref getCount);
		if (getFailure is not null)
		{
			throw getFailure;
		}

		return new(Entries.TryGetValue(cacheKey, out var cacheEntry) ? cacheEntry as CacheEntry<T> : null);
	}

	public ValueTask SetAsync<T>(string cacheKey, CacheEntry<T> cacheEntry)
	{
		Record(nameof(SetAsync), cacheKey);
		Interlocked.Increment(ref setCount);
		Entries[cacheKey] = cacheEntry;
		return new();
	}

	public ValueTask EvictAsync(string cacheKey)
	{
		Record(nameof(EvictAsync), cacheKey);
		Interlocked.Increment(ref evictCount);
		Entries.TryRemove(cacheKey, out _);
		return new();
	}

	public ValueTask FlushAsync()
	{
		Record(nameof(FlushAsync), string.Empty);
		Interlocked.Increment(ref flushCount);
		Entries.Clear();
		return new();
	}

	public ValueTask CleanupAsync()
	{
		Record(nameof(CleanupAsync), string.Empty);
		Interlocked.Increment(ref cleanupCount);
		return new();
	}
}
