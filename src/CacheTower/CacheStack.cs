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
	private readonly EvictionTracker EvictionTracker = new();
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

		for (int i = 0, l = CacheLayers.Length; i < l; i++)
		{
			var layer = CacheLayers[i];
			await layer.EvictAsync(cacheKey).ConfigureAwait(false);
		}

		EvictionTracker.MarkEvicted(cacheKey);

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
		ThrowIfDisposed();

		if (cacheKey == null)
		{
			throw new ArgumentNullException(nameof(cacheKey));
		}

		if (valueFactory == null)
		{
			throw new ArgumentNullException(nameof(valueFactory));
		}

		var currentTime = DateTimeProvider.Now;
		var cacheEntryPoint = await GetWithLayerIndexAsync<T>(cacheKey).ConfigureAwait(false);
		var cacheEntryStatus = CacheEntryStatus.Stale;
		if (cacheEntryPoint != default)
		{
			if (cacheEntryPoint.CacheEntry.Expiry > currentTime)
			{
				ScheduleBackgroundMaintenance(cacheEntryPoint.LayerIndex, cacheKey, cacheEntryPoint.CacheEntry, valueFactory, settings, currentTime);
				return cacheEntryPoint.CacheEntry.Value!;
			}
			else
			{
				//Refresh the value in the current thread because we only have expired data (we never return expired data)
				cacheEntryStatus = CacheEntryStatus.Expired;
			}
		}
		else
		{
			//Refresh the value in the current thread because we have no existing data
			cacheEntryStatus = CacheEntryStatus.Miss;
		}

		return (await RefreshValueAsync(cacheKey, valueFactory, settings, cacheEntryStatus).ConfigureAwait(false))!.Value!;
	}

	/// <inheritdoc/>
	public async ValueTask<CacheReadResult<T>> GetOrSetAsync<T>(string cacheKey, Func<T, Task<T>> valueFactory, CacheSettings settings, TimeSpan staleWindow, CancellationToken cancellationToken = default)
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

		if (staleWindow < TimeSpan.Zero)
		{
			throw new ArgumentOutOfRangeException(nameof(staleWindow), "The stale-while-revalidate window cannot be negative.");
		}

		cancellationToken.ThrowIfCancellationRequested();

		var currentTime = DateTimeProvider.Now;
		var cacheEntryPoint = await GetWithLayerIndexAsync<T>(cacheKey).ConfigureAwait(false);
		var cacheEntryStatus = CacheEntryStatus.Miss;
		if (cacheEntryPoint != default)
		{
			var cacheEntry = cacheEntryPoint.CacheEntry;
			var origin = cacheEntryPoint.LayerIndex == 0 ? CacheValueOrigin.LocalLayer : CacheValueOrigin.FallbackLayer;
			if (cacheEntry.Expiry > currentTime)
			{
				ScheduleBackgroundMaintenance(cacheEntryPoint.LayerIndex, cacheKey, cacheEntry, valueFactory, settings, currentTime);
				return new(cacheEntry.Value, new(origin, CacheValueFreshness.Fresh));
			}

			if (currentTime - cacheEntry.Expiry <= staleWindow)
			{
				//The entry is expired but still inside the stale-while-revalidate window:
				//serve the stale value and refresh in the background.
				//The key lock inside RefreshValueAsync ensures only one refresh per key is triggered.
				_ = ObserveBackgroundRefreshAsync(RefreshValueAsync(cacheKey, valueFactory, settings, CacheEntryStatus.Stale).AsTask(), cacheKey);
				return new(cacheEntry.Value, new(origin, CacheValueFreshness.Stale));
			}

			//Refresh the value in the current thread because the stale-while-revalidate window has passed
			cacheEntryStatus = CacheEntryStatus.Expired;
		}

		var refreshedEntry = await AwaitSharedRefreshAsync(RefreshValueAsync(cacheKey, valueFactory, settings, cacheEntryStatus), cacheKey, cancellationToken).ConfigureAwait(false);
		return new(refreshedEntry!.Value, new(CacheValueOrigin.Factory, CacheValueFreshness.Fresh));
	}

	/// <summary>
	/// Schedules the background maintenance for a fresh cache hit: a background refresh when the
	/// entry is stale (per <see cref="CacheSettings.StaleAfter"/>) or a back-population to upper
	/// cache layers when the entry was found in a lower layer.
	/// </summary>
	private void ScheduleBackgroundMaintenance<T>(int layerIndex, string cacheKey, CacheEntry<T> cacheEntry, Func<T, Task<T>> valueFactory, CacheSettings settings, DateTime currentTime)
	{
		if (settings.StaleAfter.HasValue && cacheEntry.GetStaleDate(settings) < currentTime)
		{
			//If the cache entry is stale, refresh the value in the background
			_ = ObserveBackgroundRefreshAsync(RefreshValueAsync(cacheKey, valueFactory, settings, CacheEntryStatus.Stale).AsTask(), cacheKey);
		}
		else if (layerIndex > 0)
		{
			//If a lower-level cache is missing the latest data, attempt to set it in the background
			_ = BackPopulateCacheAsync(layerIndex, cacheKey, cacheEntry);
		}
	}

	/// <summary>
	/// Awaits a shared refresh, abandoning the wait (but not the refresh) when the caller is cancelled.
	/// The refresh itself always runs to completion so other callers waiting on the same key are unaffected.
	/// </summary>
	private async ValueTask<CacheEntry<T>?> AwaitSharedRefreshAsync<T>(ValueTask<CacheEntry<T>?> refresh, string cacheKey, CancellationToken cancellationToken)
	{
		if (!cancellationToken.CanBeCanceled)
		{
			return await refresh.ConfigureAwait(false);
		}

		var refreshTask = refresh.AsTask();
		if (await Task.WhenAny(refreshTask, Task.Delay(Timeout.Infinite, cancellationToken)).ConfigureAwait(false) == refreshTask)
		{
			return await refreshTask.ConfigureAwait(false);
		}

		//The caller was cancelled; the shared refresh continues in the background so other
		//callers waiting on the same key still receive the refreshed value.
		_ = ObserveBackgroundRefreshAsync(refreshTask, cacheKey);
		cancellationToken.ThrowIfCancellationRequested();
		throw new OperationCanceledException(cancellationToken);
	}

	/// <summary>
	/// Observes a background refresh task, logging any failure. A failed refresh never writes to
	/// the cache, so the existing entry and its stale-while-revalidate window are left unchanged.
	/// </summary>
	private async Task ObserveBackgroundRefreshAsync<T>(Task<CacheEntry<T>?> refreshTask, string cacheKey)
	{
		try
		{
			await refreshTask.ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			Logger.LogWarning(ex, "Background refresh for CacheKey={CacheKey} failed. The existing cache entry is unchanged.", cacheKey);
		}
	}

	private async ValueTask BackPopulateCacheAsync<T>(int fromIndexExclusive, string cacheKey, CacheEntry<T> cacheEntry)
	{
		if (KeyLock.AcquireLock(cacheKey))
		{
			try
			{
				for (; --fromIndexExclusive >= 0;)
				{
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

	private async ValueTask<CacheEntry<T>?> RefreshValueAsync<T>(string cacheKey, Func<T, Task<T>> asyncValueFactory, CacheSettings settings, CacheEntryStatus entryStatus)
	{
		if (KeyLock.AcquireLock(cacheKey))
		{
			var evictionVersion = EvictionTracker.GetVersion(cacheKey);
			try
			{
				var previousEntry = await GetAsync<T>(cacheKey).ConfigureAwait(false);
				if (previousEntry != default && entryStatus == CacheEntryStatus.Miss && previousEntry.Expiry >= DateTimeProvider.Now)
				{
					//The Cache Stack will always return an unexpired value if one exists.
					//If we are told to refresh because one doesn't and we find one, we return the existing value, ignoring the refresh.
					//This can happen due to the race condition of getting the values out of the cache.
					//We can only do any of this because we have the local lock.
					KeyLock.ReleaseLock(cacheKey, previousEntry);
					return previousEntry;
				}

				await using var distributedLock = await Extensions.AwaitAccessAsync(cacheKey).ConfigureAwait(false);

				CacheEntry<T>? cacheEntry;
				if (distributedLock.IsLockOwner)
				{
					var oldValue = previousEntry != default ? previousEntry.Value : default;
					var refreshedValue = await asyncValueFactory(oldValue!).ConfigureAwait(false);
					cacheEntry = new CacheEntry<T>(refreshedValue, settings.TimeToLive);
					var cacheUpdateType = entryStatus switch
					{
						CacheEntryStatus.Miss => CacheUpdateType.AddEntry,
						_ => CacheUpdateType.AddOrUpdateEntry
					};
					if (EvictionTracker.GetVersion(cacheKey) == evictionVersion)
					{
						await InternalSetAsync(cacheKey, cacheEntry, cacheUpdateType).ConfigureAwait(false);
						EvictionTracker.TryReset(cacheKey, evictionVersion);
					}
					else
					{
						//The entry was evicted while the refresh was running; the eviction wins
						//so the refreshed value is not cached and the evicted entry is not resurrected.
						Logger.LogDebug("CacheKey={CacheKey} was evicted during its refresh. The refreshed value will not be cached.", cacheKey);
					}

					KeyLock.ReleaseLock(cacheKey, cacheEntry);
				}
				else
				{
					cacheEntry = await GetAsync<T>(cacheKey).ConfigureAwait(false);
				}

				return cacheEntry;
			}
			catch (Exception e)
			{
				KeyLock.ReleaseLock(cacheKey, e);
				throw;
			}
		}
		else if (entryStatus != CacheEntryStatus.Stale)
		{
			//Last minute check to confirm whether waiting is required
			var currentEntry = await GetAsync<T>(cacheKey).ConfigureAwait(false);
			if (currentEntry != null && currentEntry.GetStaleDate(settings) > DateTimeProvider.Now)
			{
				KeyLock.ReleaseLock(cacheKey, currentEntry);
				return currentEntry;
			}

			//Lock until we are notified to be unlocked
			return await KeyLock.WaitAsync(cacheKey).ConfigureAwait(false) as CacheEntry<T>;
		}

		return default;
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
