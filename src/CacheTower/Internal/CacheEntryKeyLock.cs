using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace CacheTower.Internal;

internal readonly struct CacheEntryKeyLock
{
	private sealed class LockWaiters
	{
		public readonly List<Waiter> Waiters = new();
	}

	private sealed class Waiter : TaskCompletionSource<ICacheEntry>
	{
		public CancellationTokenRegistration Registration;

		public Waiter() : base(TaskCreationOptions.RunContinuationsAsynchronously)
		{
		}
	}

	private readonly Dictionary<string, LockWaiters> keyLocks = new(StringComparer.Ordinal);

	public CacheEntryKeyLock() { }

	public bool AcquireLock(string cacheKey)
	{
		lock (keyLocks)
		{
#if NETSTANDARD2_0
			var hasLock = !keyLocks.ContainsKey(cacheKey);
			if (hasLock)
			{
				keyLocks[cacheKey] = new LockWaiters();
			}
			return hasLock;
#elif NETSTANDARD2_1
			return keyLocks.TryAdd(cacheKey, new LockWaiters());
#endif
		}
	}

	public Task<ICacheEntry> WaitAsync(string cacheKey, CancellationToken cancellationToken = default)
	{
		var waiter = new Waiter();

		if (cancellationToken.IsCancellationRequested)
		{
			waiter.TrySetCanceled(cancellationToken);
			return waiter.Task;
		}

		lock (keyLocks)
		{
			if (!keyLocks.TryGetValue(cacheKey, out var lockWaiters))
			{
				waiter.TrySetCanceled(CancellationToken.None);
				return waiter.Task;
			}

			waiter.Registration = cancellationToken.Register(state =>
			{
				var canceledWaiter = (Waiter)state!;
				if (canceledWaiter.TrySetCanceled(CancellationToken.None))
				{
					RemoveWaiter(cacheKey, canceledWaiter);
				}
			}, waiter);

			lockWaiters.Waiters.Add(waiter);
		}

		return waiter.Task;
	}

	private void RemoveWaiter(string cacheKey, Waiter waiter)
	{
		lock (keyLocks)
		{
			if (keyLocks.TryGetValue(cacheKey, out var lockWaiters))
			{
				lockWaiters.Waiters.Remove(waiter);
			}
		}
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private bool TryRemove(string cacheKey, out LockWaiters? lockWaiters)
	{
		lock (keyLocks)
		{
#if NETSTANDARD2_0
			if (keyLocks.TryGetValue(cacheKey, out lockWaiters))
			{
				keyLocks.Remove(cacheKey);
				return true;
			}
			return false;
#elif NETSTANDARD2_1
			return keyLocks.Remove(cacheKey, out lockWaiters);
#endif
		}
	}

	public void ReleaseLock(string cacheKey, ICacheEntry cacheEntry)
	{
		if (TryRemove(cacheKey, out var lockWaiters))
		{
			foreach (var waiter in lockWaiters!.Waiters)
			{
				waiter.Registration.Dispose();
				waiter.TrySetResult(cacheEntry);
			}
		}
	}

	public void ReleaseLock(string cacheKey, Exception exception)
	{
		if (TryRemove(cacheKey, out var lockWaiters))
		{
			foreach (var waiter in lockWaiters!.Waiters)
			{
				waiter.Registration.Dispose();
				waiter.TrySetException(exception);
			}
		}
	}
}
