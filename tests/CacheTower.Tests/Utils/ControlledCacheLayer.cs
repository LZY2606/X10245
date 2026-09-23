using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using CacheTower.Internal;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CacheTower.Tests.Utils;

/// <summary>
/// A controllable <see cref="ICacheLayer"/> for tests: in-memory storage with call counting,
/// failure injection, a stable-ordered operation log and async gates to control timing.
/// It only ever talks to the cache stack through the public <see cref="ICacheLayer"/> contract.
/// </summary>
internal sealed class ControlledCacheLayer : ICacheLayer
{
	private readonly object SyncRoot = new();
	private readonly Dictionary<string, ICacheEntry> Entries = new(StringComparer.Ordinal);
	private readonly List<string> OperationLog = new();

	private int getCallCount;
	private int setCallCount;
	private int evictCallCount;

	public ControlledCacheLayer(string name)
	{
		Name = name;
	}

	public string Name { get; }
	public bool IsAvailable { get; set; } = true;
	public Exception? GetException { get; set; }
	public Exception? SetException { get; set; }
	public TaskCompletionSource<bool>? GetGate { get; set; }
	public TaskCompletionSource<bool>? SetGate { get; set; }

	public int GetCallCount => Volatile.Read(ref getCallCount);
	public int SetCallCount => Volatile.Read(ref setCallCount);
	public int EvictCallCount => Volatile.Read(ref evictCallCount);

	public IReadOnlyList<string> Operations
	{
		get
		{
			lock (SyncRoot)
			{
				return OperationLog.ToArray();
			}
		}
	}

	private void Record(string operation, string? cacheKey = null)
	{
		lock (SyncRoot)
		{
			OperationLog.Add(cacheKey is null ? $"{Name}:{operation}" : $"{Name}:{operation}:{cacheKey}");
		}
	}

	/// <summary>
	/// Reads an entry without counting or logging - used to assert internal invariants.
	/// </summary>
	public CacheEntry<T>? Peek<T>(string cacheKey)
	{
		lock (SyncRoot)
		{
			return Entries.TryGetValue(cacheKey, out var entry) ? entry as CacheEntry<T> : null;
		}
	}

	public async ValueTask<CacheEntry<T>?> GetAsync<T>(string cacheKey)
	{
		Interlocked.Increment(ref getCallCount);
		Record("Get", cacheKey);
		if (GetGate is { } gate)
		{
			await gate.Task.ConfigureAwait(false);
		}
		if (GetException is { } exception)
		{
			throw exception;
		}
		lock (SyncRoot)
		{
			return Entries.TryGetValue(cacheKey, out var entry) ? entry as CacheEntry<T> : null;
		}
	}

	public async ValueTask SetAsync<T>(string cacheKey, CacheEntry<T> cacheEntry)
	{
		Interlocked.Increment(ref setCallCount);
		Record("Set", cacheKey);
		if (SetGate is { } gate)
		{
			await gate.Task.ConfigureAwait(false);
		}
		if (SetException is { } exception)
		{
			throw exception;
		}
		lock (SyncRoot)
		{
			Entries[cacheKey] = cacheEntry;
		}
	}

	public ValueTask EvictAsync(string cacheKey)
	{
		Interlocked.Increment(ref evictCallCount);
		Record("Evict", cacheKey);
		lock (SyncRoot)
		{
			Entries.Remove(cacheKey);
		}
		return new ValueTask();
	}

	public ValueTask FlushAsync()
	{
		Record("Flush");
		lock (SyncRoot)
		{
			Entries.Clear();
		}
		return new ValueTask();
	}

	public ValueTask CleanupAsync()
	{
		Record("Cleanup");
		var currentTime = DateTimeProvider.Now;
		lock (SyncRoot)
		{
			var expiredKeys = new List<string>();
			foreach (var pair in Entries)
			{
				if (pair.Value.Expiry < currentTime)
				{
					expiredKeys.Add(pair.Key);
				}
			}
			foreach (var key in expiredKeys)
			{
				Entries.Remove(key);
			}
		}
		return new ValueTask();
	}

	public ValueTask<bool> IsAvailableAsync(string cacheKey)
	{
		return new ValueTask<bool>(IsAvailable);
	}
}

/// <summary>
/// A controllable value factory for tests: counts calls, supports gating and one-shot failure injection.
/// </summary>
internal sealed class ControlledFactory
{
	private int callCount;
	private readonly Func<int, int> compute;

	public ControlledFactory(Func<int, int>? compute = null)
	{
		this.compute = compute ?? (call => call);
	}

	public int CallCount => Volatile.Read(ref callCount);

	/// <summary>Signalled when a factory call has started.</summary>
	public TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
	/// <summary>When set, every factory call waits for this gate before completing.</summary>
	public TaskCompletionSource<bool>? Gate { get; set; }
	/// <summary>When set, the next factory call throws this exception (recoverable injection) and the slot clears.</summary>
	public Exception? NextFailure { get; set; }

	public async Task<int> Invoke(int oldValue)
	{
		var call = Interlocked.Increment(ref callCount);
		Entered.TrySetResult(true);
		if (Gate is { } gate)
		{
			await gate.Task.ConfigureAwait(false);
		}
		if (NextFailure is { } failure)
		{
			NextFailure = null;
			throw failure;
		}
		return compute(call);
	}
}

internal static class TestWait
{
	/// <summary>
	/// Polls <paramref name="condition"/> until it holds, failing with the given context (first violation point) on timeout.
	/// </summary>
	public static async Task UntilAsync(Func<bool> condition, string context, int timeoutMs = 10000)
	{
		var stopwatch = Stopwatch.StartNew();
		while (!condition())
		{
			if (stopwatch.ElapsedMilliseconds > timeoutMs)
			{
				Assert.Fail($"Timed out after {timeoutMs}ms waiting for condition. Context: {context}");
			}
			await Task.Delay(20);
		}
	}
}
