using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CacheTower.Internal;

internal sealed class CacheMutationCoordinator
{
	internal readonly record struct CacheMutationClaim(int KeyVersion, int FlushVersion);

	private sealed class KeyState
	{
		public SemaphoreSlim Semaphore { get; } = new(1, 1);
		public int Version { get; set; }
		public int PendingOperations { get; set; }
	}

	internal sealed class CacheKeyOperation : IDisposable
	{
		private readonly CacheMutationCoordinator Coordinator;
		private bool Disposed;

		public CacheKeyOperation(CacheMutationCoordinator coordinator, string cacheKey, KeyState keyState, int keyVersion, int flushVersion)
		{
			Coordinator = coordinator;
			CacheKey = cacheKey;
			KeyState = keyState;
			KeyVersion = keyVersion;
			FlushVersionSnapshot = flushVersion;
		}

		public string CacheKey { get; }
		public KeyState KeyState { get; }
		public int KeyVersion { get; }
		public int FlushVersionSnapshot { get; }

		public CacheKeyOperation Branch()
		{
			lock (Coordinator.StateLock)
			{
				KeyState.PendingOperations++;
				return new CacheKeyOperation(Coordinator, CacheKey, KeyState, KeyVersion, FlushVersionSnapshot);
			}
		}

		public bool Matches(CacheMutationClaim claim)
		{
			return KeyState.Version == claim.KeyVersion && Coordinator.FlushVersion == claim.FlushVersion;
		}

		public bool IsCurrent()
		{
			return KeyState.Version == KeyVersion && Coordinator.FlushVersion == FlushVersionSnapshot;
		}

		public void Dispose()
		{
			if (Disposed)
			{
				return;
			}

			Disposed = true;
			Coordinator.EndOperation(this);
		}
	}

	private sealed class WriteBarrier : IDisposable
	{
		private readonly CacheMutationCoordinator Coordinator;
		private readonly KeyState State;
		private readonly bool HoldsKeyLock;
		private bool Disposed;

		public WriteBarrier(CacheMutationCoordinator coordinator, KeyState state, bool holdsKeyLock)
		{
			Coordinator = coordinator;
			State = state;
			HoldsKeyLock = holdsKeyLock;
		}

		public void Dispose()
		{
			if (Disposed)
			{
				return;
			}

			if (HoldsKeyLock)
			{
				State.Semaphore.Release();
			}
			Coordinator.FlushLock.Release();
			Disposed = true;
		}
	}

	private readonly object StateLock = new();
	private readonly Dictionary<string, KeyState> KeyStates = new(StringComparer.Ordinal);
	private readonly SemaphoreSlim FlushLock = new(1, 1);
	private int FlushVersion;

	public CacheKeyOperation BeginOperation(string cacheKey)
	{
		lock (StateLock)
		{
			if (!KeyStates.TryGetValue(cacheKey, out var state))
			{
				state = new KeyState();
				KeyStates.Add(cacheKey, state);
			}

			state.PendingOperations++;
			return new CacheKeyOperation(this, cacheKey, state, state.Version, FlushVersion);
		}
	}

	public async ValueTask<CacheMutationClaim?> ClaimMutationAsync(CacheKeyOperation operation)
	{
		await operation.KeyState.Semaphore.WaitAsync().ConfigureAwait(false);
		try
		{
			if (!operation.IsCurrent())
			{
				return null;
			}

			operation.KeyState.Version++;
			return new CacheMutationClaim(operation.KeyState.Version, FlushVersion);
		}
		finally
		{
			operation.KeyState.Semaphore.Release();
		}
	}

	public ValueTask<IDisposable?> AcquireCommitBarrierAsync(CacheKeyOperation operation, CacheMutationClaim? claim)
	{
		if (!claim.HasValue)
		{
			return new ValueTask<IDisposable?>(default(IDisposable));
		}

		return AcquireCommitBarrierInternalAsync(operation, claim.Value);
	}

	private async ValueTask<IDisposable?> AcquireCommitBarrierInternalAsync(CacheKeyOperation operation, CacheMutationClaim claim)
	{
		await operation.KeyState.Semaphore.WaitAsync().ConfigureAwait(false);
		var holdsKeyLock = true;

		try
		{
			await FlushLock.WaitAsync().ConfigureAwait(false);
			try
			{
				if (!operation.Matches(claim))
				{
					FlushLock.Release();
					operation.KeyState.Semaphore.Release();
					holdsKeyLock = false;
					return null;
				}

				return new WriteBarrier(this, operation.KeyState, true);
			}
			catch
			{
				FlushLock.Release();
				throw;
			}
		}
		catch
		{
			if (holdsKeyLock)
			{
				operation.KeyState.Semaphore.Release();
			}
			throw;
		}
	}

	public ValueTask<IDisposable?> AcquireBackfillBarrierAsync(CacheKeyOperation operation)
	{
		return AcquireSerializationBarrierAsync(operation, false);
}

	public ValueTask<IDisposable> AcquireEvictionBarrierAsync(CacheKeyOperation operation)
	{
		var barrierTask = AcquireEvictionBarrierInternalAsync(operation);
		return CastBarrierAsync(barrierTask);
	}

	private async ValueTask<IDisposable?> AcquireEvictionBarrierInternalAsync(CacheKeyOperation operation)
	{
		await operation.KeyState.Semaphore.WaitAsync().ConfigureAwait(false);
		var holdsKeyLock = true;

		try
		{
			await FlushLock.WaitAsync().ConfigureAwait(false);
			try
			{
				operation.KeyState.Version++;
				return new WriteBarrier(this, operation.KeyState, true);
			}
			catch
			{
				FlushLock.Release();
				throw;
			}
		}
		catch
		{
			if (holdsKeyLock)
			{
				operation.KeyState.Semaphore.Release();
			}
			throw;
		}
	}

	private static async ValueTask<IDisposable> CastBarrierAsync(ValueTask<IDisposable?> barrierTask)
	{
		return await barrierTask.ConfigureAwait(false) ?? throw new InvalidOperationException();
	}

	private async ValueTask<IDisposable?> AcquireSerializationBarrierAsync(CacheKeyOperation operation, bool incrementVersion)
	{
		await operation.KeyState.Semaphore.WaitAsync().ConfigureAwait(false);
		var holdsKeyLock = true;

		try
		{
			await FlushLock.WaitAsync().ConfigureAwait(false);
			try
			{
				if (!operation.IsCurrent())
				{
					FlushLock.Release();
					operation.KeyState.Semaphore.Release();
					holdsKeyLock = false;
					return null;
				}

				if (incrementVersion)
				{
					operation.KeyState.Version++;
				}

				return new WriteBarrier(this, operation.KeyState, true);
			}
			catch
			{
				FlushLock.Release();
				throw;
			}
		}
		catch
		{
			if (holdsKeyLock)
			{
				operation.KeyState.Semaphore.Release();
			}
			throw;
		}
	}

	public async ValueTask<IDisposable> AcquireFlushBarrierAsync()
	{
		await FlushLock.WaitAsync().ConfigureAwait(false);
		FlushVersion++;
		return new WriteBarrier(this, null!, false);
	}

	private void EndOperation(CacheKeyOperation operation)
	{
		var removeState = false;

		lock (StateLock)
		{
			operation.KeyState.PendingOperations--;
			if (operation.KeyState.PendingOperations == 0
				&& KeyStates.TryGetValue(operation.CacheKey, out var currentState)
				&& ReferenceEquals(currentState, operation.KeyState))
			{
				KeyStates.Remove(operation.CacheKey);
				removeState = true;
			}
		}

		if (removeState && operation.KeyState.Semaphore.CurrentCount == 1)
		{
			operation.KeyState.Semaphore.Dispose();
		}
	}
}
