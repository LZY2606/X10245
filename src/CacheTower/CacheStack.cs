using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CacheTower.Extensions;
using CacheTower.Internal;
using CacheTower.Serializers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CacheTower;

/// <summary>
/// Options for configuring a <see cref="CacheStack"/>.
/// </summary>
public readonly record struct CacheStackOptions
{
	/// <summary>
	/// The cache layers to use for the cache stack.
	/// </summary>
	public ICacheLayer[] CacheLayers { get; }

	/// <summary>
	/// Creates a new <see cref="CacheStackOptions"/> for configuring a <see cref="CacheStack"/>.
	/// </summary>
	/// <param name="cacheLayers">The cache layers to use for the cache stack. The layers should be ordered from the highest priority to the lowest. At least one cache layer is required.</param>
	/// <exception cref="ArgumentException"></exception>
	public CacheStackOptions(params ICacheLayer[] cacheLayers)
	{
		if (cacheLayers is null or { Length: 0 })
		{
			throw new ArgumentException("There must be at least one cache layer", nameof(cacheLayers));
		}

		CacheLayers = cacheLayers;
	}

	/// <summary>
	/// The cache extensions to use for the cache stack.
	/// </summary>
	public ICacheExtension[] Extensions { get; init; } = Array.Empty<ICacheExtension>();
}

/// <summary>
/// A <see cref="CacheStack"/> is the backbone of caching for Cache Tower. This manages coordination between the various cache layers, manages the cache extensions and handles background refreshing.
/// </summary>
public class CacheStack : ICacheStack, IFlushableCacheStack, IExtendableCacheStack, IAsyncDisposable
{
	private bool Disposed;

	private readonly CacheEntryKeyLock KeyLock = new();
	private readonly object EvictionVersionLock = new();
	private readonly Dictionary<string, long> EvictionVersions = new(StringComparer.Ordinal);
	private long FlushGeneration;
	private readonly ILogger<CacheStack> Logger;
	private readonly ICacheLayer[] CacheLayers;
	private readonly ExtensionContainer Extensions;

	/// <summary>
	/// Creates a new <see cref="CacheStack"/> with the provided <paramref name="options"/>.
	/// </summary>
	/// <param name="logger">The internal logger to use. If none is provided, a null logger will be used instead.</param>
	/// <param name="options">The <see cref="CacheStackOptions"/> to configure this cache stack.</param>
	public CacheStack(ILogger<CacheStack>? logger, CacheStackOptions options)
	{
		Logger = logger ?? NullLogger<CacheStack>.Instance;
		CacheLayers = options.CacheLayers;

		Extensions = new ExtensionContainer(options.Extensions);
		Extensions.Register(this);
	}

	/// <summary>
	/// Creates a new <see cref="CacheStack"/> with the given <paramref name="cacheLayers"/> and <paramref name="extensions"/>.
	/// </summary>
	/// <param name="logger">The internal logger to use.</param>
	/// <param name="cacheLayers">The cache layers to use for the current cache stack. The layers should be ordered from the highest priority to the lowest. At least one cache layer is required.</param>
	/// <param name="extensions">The cache extensions to use for the current cache stack.</param>
	[Obsolete("Use constructor with 'CacheStackOptions'")]
	public CacheStack(ILogger<CacheStack>? logger, ICacheLayer[] cacheLayers, ICacheExtension[] extensions) : this(logger, new(cacheLayers) { Extensions = extensions })
	{
	}

	/// <summary>
	/// Helper for throwing if disposed.
	/// </summary>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	protected void ThrowIfDisposed()
	{
		if (Disposed)
		{
			ThrowDisposedException();
		}

		static void ThrowDisposedException() => throw new ObjectDisposedException(nameof(CacheStack));
	}

	/// <inheritdoc/>
	public IReadOnlyList<ICacheLayer> GetCacheLayers()
	{
		return CacheLayers;
	}

	/// <inheritdoc/>
	public async ValueTask FlushAsync()
	{
		ThrowIfDisposed();

		lock (EvictionVersionLock)
		{
			FlushGeneration++;
			EvictionVersions.Clear();
		}

		for (int i = 0, l = CacheLayers.Length; i < l; i++)
		{
			var layer = CacheLayers[i];
			await layer.FlushAsync().ConfigureAwait(false);
		}

		await Extensions.OnCacheFlushAsync().ConfigureAwait(false);
	}

	/// <inheritdoc/>
	public async ValueTask CleanupAsync()
	{
		ThrowIfDisposed();

		for (int i = 0, l = CacheLayers.Length; i < l; i++)
		{
			var layer = CacheLayers[i];
			await layer.CleanupAsync().ConfigureAwait(false);
		}
	}

	/// <inheritdoc/>
	public async ValueTask EvictAsync(string cacheKey)
	{
		ThrowIfDisposed();

		if (cacheKey == null)
		{
			throw new ArgumentNullException(nameof(cacheKey));
		}

		BumpEvictionVersion(cacheKey);

		for (int i = 0, l = CacheLayers.Length; i < l; i++)
		{
			var layer = CacheLayers[i];
			await layer.EvictAsync(cacheKey).ConfigureAwait(false);
		}

		await Extensions.OnCacheEvictionAsync(cacheKey).ConfigureAwait(false);
	}

	/// <inheritdoc/>
	public async ValueTask<CacheEntry<T>> SetAsync<T>(string cacheKey, T value, TimeSpan timeToLive)
	{
		ThrowIfDisposed();

		if (cacheKey == null)
		{
			throw new ArgumentNullException(nameof(cacheKey));
		}

		var cacheEntry = new CacheEntry<T>(value, timeToLive);
		await InternalSetAsync(cacheKey, cacheEntry, CacheUpdateType.AddOrUpdateEntry).ConfigureAwait(false);
		return cacheEntry;
	}

	/// <inheritdoc/>
	public async ValueTask SetAsync<T>(string cacheKey, CacheEntry<T> cacheEntry)
	{
		ThrowIfDisposed();

		if (cacheKey == null)
		{
			throw new ArgumentNullException(nameof(cacheKey));
		}

		if (cacheEntry == null)
		{
			throw new ArgumentNullException(nameof(cacheEntry));
		}

		await InternalSetAsync(cacheKey, cacheEntry, CacheUpdateType.AddOrUpdateEntry).ConfigureAwait(false);
	}

	private async ValueTask InternalSetAsync<T>(string cacheKey, CacheEntry<T> cacheEntry, CacheUpdateType cacheUpdateType)
	{
		for (int i = 0, l = CacheLayers.Length; i < l; i++)
		{
			var layer = CacheLayers[i];

			try
			{
				await layer.SetAsync(cacheKey, cacheEntry).ConfigureAwait(false);
			}
			catch (CacheSerializationException ex)
			{
				Logger.LogWarning(ex, "Unable to set CacheKey={CacheKey} on CacheLayer={CacheLayer} due to an exception. This will result in cache misses on this layer.", cacheKey, layer.GetType());
			}
		}

		await Extensions.OnCacheUpdateAsync(cacheKey, cacheEntry.Expiry, cacheUpdateType).ConfigureAwait(false);
	}

	/// <inheritdoc/>
	public async ValueTask<CacheEntry<T>?> GetAsync<T>(string cacheKey)
	{
		ThrowIfDisposed();

		if (cacheKey == null)
		{
			throw new ArgumentNullException(nameof(cacheKey));
		}

		for (var layerIndex = 0; layerIndex < CacheLayers.Length; layerIndex++)
		{
			var layer = CacheLayers[layerIndex];
			if (await layer.IsAvailableAsync(cacheKey).ConfigureAwait(false))
			{
				try
				{
					var cacheEntry = await layer.GetAsync<T>(cacheKey).ConfigureAwait(false);
					if (cacheEntry != default)
					{
						return cacheEntry;
					}
				}
				catch (CacheSerializationException ex)
				{
					Logger.LogWarning(ex, "Unable to retrieve CacheKey={CacheKey} from CacheLayer={CacheLayer} due to an exception. This layer will be skipped.", cacheKey, layer.GetType());
				}
			}
		}

		return default;
	}

	/// <inheritdoc/>
	public async ValueTask<CacheEntry<T>?> GetAsync<T>(string cacheKey, bool backPopulate)
	{
		if (!backPopulate)
		{
			return await GetAsync<T>(cacheKey).ConfigureAwait(false);
		}

		ThrowIfDisposed();

		if (cacheKey == null)
		{
			throw new ArgumentNullException(nameof(cacheKey));
		}

		var cacheEntryPoint = await GetWithLayerIndexAsync<T>(cacheKey).ConfigureAwait(false);
		if (cacheEntryPoint == default)
		{
			return default;
		}

		if (cacheEntryPoint.LayerIndex == 0)
		{
			return cacheEntryPoint.CacheEntry;
		}

		_ = BackPopulateCacheAsync(cacheEntryPoint.LayerIndex, cacheKey, cacheEntryPoint.CacheEntry);

		return cacheEntryPoint.CacheEntry;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private async ValueTask<(int LayerIndex, CacheEntry<T> CacheEntry)> GetWithLayerIndexAsync<T>(string cacheKey)
	{
		for (var layerIndex = 0; layerIndex < CacheLayers.Length; layerIndex++)
		{
			var layer = CacheLayers[layerIndex];
			if (await layer.IsAvailableAsync(cacheKey).ConfigureAwait(false))
			{
				try
				{
					var cacheEntry = await layer.GetAsync<T>(cacheKey).ConfigureAwait(false);
					if (cacheEntry != default)
					{
						return (layerIndex, cacheEntry);
					}
				}
				catch (CacheSerializationException ex)
				{
					Logger.LogWarning(ex, "Unable to retrieve CacheKey={CacheKey} from CacheLayer={CacheLayer} due to an exception. This layer will be skipped.", cacheKey, layer.GetType());
				}
			}
		}

		return default;
	}

	/// <inheritdoc/>
	public async ValueTask<T> GetOrSetAsync<T>(string cacheKey, Func<T, Task<T>> valueFactory, CacheSettings settings)
	{
		var result = await GetOrSetCoreAsync(cacheKey, valueFactory, settings, CancellationToken.None).ConfigureAwait(false);
		return result.Value;
	}

	/// <inheritdoc/>
	public ValueTask<CacheReadResult<T>> GetOrSetWithReceiptAsync<T>(
		string cacheKey,
		Func<T, Task<T>> valueFactory,
		CacheSettings settings,
		CancellationToken cancellationToken = default)
	{
		return GetOrSetCoreAsync(cacheKey, valueFactory, settings, cancellationToken);
	}

	private async ValueTask<CacheReadResult<T>> GetOrSetCoreAsync<T>(
		string cacheKey,
		Func<T, Task<T>> valueFactory,
		CacheSettings settings,
		CancellationToken cancellationToken)
	{
		ThrowIfDisposed();

		if (cacheKey == null)
		{
			throw new ArgumentNullException(nameof(cacheKey));
		}

		if (valueFactory == null)
		{
			throw new ArgumentNullException(nameof(valueFactory));
		}

		cancellationToken.ThrowIfCancellationRequested();

		var currentTime = DateTimeProvider.Now;
		var cacheEntryPoint = await GetWithLayerIndexAsync<T>(cacheKey).ConfigureAwait(false);
		if (cacheEntryPoint != default)
		{
			var cacheEntry = cacheEntryPoint.CacheEntry;
			if (cacheEntry.Expiry > currentTime)
			{
				var isStale = settings.StaleAfter.HasValue && cacheEntry.GetStaleDate(settings) < currentTime;
				if (isStale)
				{
					//If the cache entry is stale, refresh the value in the background (single-flighted via the key lock)
					_ = RefreshValueAsync(cacheKey, valueFactory, settings, CacheEntryStatus.Stale, CancellationToken.None);
				}
				else if (cacheEntryPoint.LayerIndex > 0)
				{
					//If a lower-level cache is missing the latest data, attempt to set it in the background
					_ = BackPopulateCacheAsync(cacheEntryPoint.LayerIndex, cacheKey, cacheEntry);
				}

				return new CacheReadResult<T>(
					cacheEntry.Value!,
					CacheReadReceipt.ForLayer(cacheEntryPoint.LayerIndex, isStale, cacheKey, cacheEntry.Expiry)
				);
			}

			if (settings.StaleWhileRevalidate.HasValue
				&& currentTime <= cacheEntry.Expiry + settings.StaleWhileRevalidate.Value)
			{
				//Opt-in stale-while-revalidate: serve the expired value within the window and trigger exactly one background refresh.
				//A failed refresh does not change the entry, so the window is never extended.
				_ = RefreshValueAsync(cacheKey, valueFactory, settings, CacheEntryStatus.Stale, CancellationToken.None);
				return new CacheReadResult<T>(
					cacheEntry.Value!,
					CacheReadReceipt.ForLayer(cacheEntryPoint.LayerIndex, true, cacheKey, cacheEntry.Expiry)
				);
			}
		}

		//No servable entry - either expired data (outside any SWR window) or a miss. Refresh in the current thread.
		var entryStatus = cacheEntryPoint != default ? CacheEntryStatus.Expired : CacheEntryStatus.Miss;
		var refreshResult = await RefreshValueAsync(cacheKey, valueFactory, settings, entryStatus, cancellationToken).ConfigureAwait(false);
		var receipt = refreshResult.FromFactory
			? CacheReadReceipt.ForFactory(cacheKey)
			: CacheReadReceipt.ForLayer(refreshResult.LayerIndex, false, cacheKey, refreshResult.Entry!.Expiry);
		return new CacheReadResult<T>(refreshResult.Entry!.Value!, receipt);
	}

	private async ValueTask BackPopulateCacheAsync<T>(int fromIndexExclusive, string cacheKey, CacheEntry<T> cacheEntry)
	{
		if (KeyLock.AcquireLock(cacheKey))
		{
			var evictionCheckpoint = CaptureEvictionCheckpoint(cacheKey);
			try
			{
				for (; --fromIndexExclusive >= 0;)
				{
					if (!IsEvictionCheckpointCurrent(cacheKey, evictionCheckpoint))
					{
						//The key was evicted (or flushed) while back-population was in flight - don't resurrect it.
						KeyLock.ReleaseLock(cacheKey, cacheEntry);
						return;
					}

					var previousLayer = CacheLayers[fromIndexExclusive];
					if (await previousLayer.IsAvailableAsync(cacheKey).ConfigureAwait(false))
					{
						await previousLayer.SetAsync(cacheKey, cacheEntry).ConfigureAwait(false);
					}
				}
			}
			finally
			{
				KeyLock.ReleaseLock(cacheKey, cacheEntry);
			}
		}
	}

	private readonly record struct RefreshOutcome<T>(CacheEntry<T>? Entry, int LayerIndex, bool FromFactory);

	private async ValueTask<RefreshOutcome<T>> RefreshValueAsync<T>(
		string cacheKey,
		Func<T, Task<T>> asyncValueFactory,
		CacheSettings settings,
		CacheEntryStatus entryStatus,
		CancellationToken cancellationToken)
	{
		if (KeyLock.AcquireLock(cacheKey))
		{
			//Record the eviction state at the start of the refresh: an eviction that races with this refresh must win.
			var evictionCheckpoint = CaptureEvictionCheckpoint(cacheKey);
			try
			{
				var previousEntryPoint = await GetWithLayerIndexAsync<T>(cacheKey).ConfigureAwait(false);
				var previousEntry = previousEntryPoint.CacheEntry;
				if (previousEntry != default && entryStatus == CacheEntryStatus.Miss && previousEntry.Expiry >= DateTimeProvider.Now)
				{
					//The Cache Stack will always return an unexpired value if one exists.
					//If we are told to refresh because one doesn't and we find one, we return the existing value, ignoring the refresh.
					//This can happen due to the race condition of getting the values out of the cache.
					//We can only do any of this because we have the local lock.
					KeyLock.ReleaseLock(cacheKey, previousEntry);
					return new RefreshOutcome<T>(previousEntry, previousEntryPoint.LayerIndex, false);
				}

				await using var distributedLock = await Extensions.AwaitAccessAsync(cacheKey).ConfigureAwait(false);

				CacheEntry<T>? cacheEntry;
				var fromFactory = false;
				var layerIndex = -1;
				if (distributedLock.IsLockOwner)
				{
					var oldValue = previousEntry != default ? previousEntry.Value : default;
					//The shared refresh itself is never tied to a caller's token - only callers waiting on it can be cancelled.
					var refreshedValue = await asyncValueFactory(oldValue!).ConfigureAwait(false);
					cacheEntry = new CacheEntry<T>(refreshedValue, settings.TimeToLive);

					if (!IsEvictionCheckpointCurrent(cacheKey, evictionCheckpoint))
					{
						//The key was evicted (or flushed) while the factory was running.
						//Do not write to the layers or notify extensions - the deletion must not be undone and shared state must not be half-updated.
						KeyLock.ReleaseLock(cacheKey, cacheEntry);
						return new RefreshOutcome<T>(cacheEntry, -1, true);
					}

					var cacheUpdateType = entryStatus switch
					{
						CacheEntryStatus.Miss => CacheUpdateType.AddEntry,
						_ => CacheUpdateType.AddOrUpdateEntry
					};
					await InternalSetAsync(cacheKey, cacheEntry, cacheUpdateType).ConfigureAwait(false);
					ConsumeEvictionCheckpoint(cacheKey, evictionCheckpoint);

					fromFactory = true;
					KeyLock.ReleaseLock(cacheKey, cacheEntry);
				}
				else
				{
					var entryPoint = await GetWithLayerIndexAsync<T>(cacheKey).ConfigureAwait(false);
					cacheEntry = entryPoint.CacheEntry;
					layerIndex = entryPoint.LayerIndex;
				}

				return new RefreshOutcome<T>(cacheEntry, layerIndex, fromFactory);
			}
			catch (Exception e)
			{
				//Releasing the lock with the failure leaves the previous entry and its expiry untouched -
				//a failed refresh cannot extend the stale window or leave any partial cache state.
				KeyLock.ReleaseLock(cacheKey, e);
				throw;
			}
		}
		else if (entryStatus != CacheEntryStatus.Stale)
		{
			//Last minute check to confirm whether waiting is required
			var currentEntryPoint = await GetWithLayerIndexAsync<T>(cacheKey).ConfigureAwait(false);
			var currentEntry = currentEntryPoint.CacheEntry;
			if (currentEntry != null && currentEntry.GetStaleDate(settings) > DateTimeProvider.Now)
			{
				KeyLock.ReleaseLock(cacheKey, currentEntry);
				return new RefreshOutcome<T>(currentEntry, currentEntryPoint.LayerIndex, false);
			}

			//Lock until we are notified to be unlocked. Only this waiter's wait is cancellable - the shared refresh keeps running.
			var waitedEntry = await KeyLock.WaitAsync(cacheKey, cancellationToken).ConfigureAwait(false) as CacheEntry<T>;
			return new RefreshOutcome<T>(waitedEntry, -1, true);
		}

		return new RefreshOutcome<T>(default, -1, false);
	}


	/// <summary>
	/// Disposes the current instance of <see cref="CacheStack"/> and all associated layers and extensions.
	/// </summary>
	/// <returns></returns>
	public async ValueTask DisposeAsync()
	{
		if (Disposed)
		{
			return;
		}

		foreach (var layer in CacheLayers)
		{
			if (layer is IDisposable disposableLayer)
			{
				disposableLayer.Dispose();
			}
			else if (layer is IAsyncDisposable asyncDisposableLayer)
			{
				await asyncDisposableLayer.DisposeAsync().ConfigureAwait(false);
			}
		}

		await Extensions.DisposeAsync().ConfigureAwait(false);

		Disposed = true;
	}
}
