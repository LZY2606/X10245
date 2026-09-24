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
		var result = await GetOrSetWithReceiptAsync(cacheKey, valueFactory, settings, CancellationToken.None).ConfigureAwait(false);
		return result.Value!;
	}

	/// <inheritdoc/>
	public async ValueTask<CacheFetchResult<T>> GetOrSetWithReceiptAsync<T>(string cacheKey, Func<T, Task<T>> valueFactory, CacheSettings settings, CancellationToken cancellationToken = default)
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
		var cacheEntryStatus = CacheEntryStatus.Stale;
		if (cacheEntryPoint != default)
		{
			var cacheEntry = cacheEntryPoint.CacheEntry;
			if (cacheEntry.Expiry > currentTime)
			{
				if (settings.StaleAfter.HasValue && cacheEntry.GetStaleDate(settings) < currentTime)
				{
					//If the cache entry is stale, refresh the value in the background
					_ = RefreshValueAsync(cacheKey, valueFactory, settings, cacheEntryStatus);
					return new CacheFetchResult<T>(cacheEntry.Value, GetSource(cacheEntryPoint.LayerIndex, isStale: true));
				}
				else
				{
					if (cacheEntryPoint.LayerIndex > 0)
					{
						//If a lower-level cache is missing the latest data, attempt to set it in the background
						_ = BackPopulateCacheAsync(cacheEntryPoint.LayerIndex, cacheKey, cacheEntry);
					}

					return new CacheFetchResult<T>(cacheEntry.Value, GetSource(cacheEntryPoint.LayerIndex, isStale: false));
				}
			}
			else if (settings.StaleWhileRevalidateAfter.HasValue && cacheEntry.Expiry + settings.StaleWhileRevalidateAfter.Value > currentTime)
			{
				//The cache entry is expired but still inside the opt-in stale-while-revalidate window.
				//Return the expired value and refresh it in the background - only one refresh per cache key happens at a time.
				//The window is measured from the entry's original expiry, so a failed refresh never extends it.
				_ = RefreshValueAsync(cacheKey, valueFactory, settings, cacheEntryStatus);
				return new CacheFetchResult<T>(cacheEntry.Value, GetSource(cacheEntryPoint.LayerIndex, isStale: true));
			}
			else
			{
				//Refresh the value in the current thread because we only have expired data (we never return expired data unless opted-in above)
				cacheEntryStatus = CacheEntryStatus.Expired;
			}
		}
		else
		{
			//Refresh the value in the current thread because we have no existing data
			cacheEntryStatus = CacheEntryStatus.Miss;
		}

		var refreshResult = await WaitForRefreshAsync(RefreshValueAsync(cacheKey, valueFactory, settings, cacheEntryStatus), cancellationToken).ConfigureAwait(false);
		return refreshResult ?? throw new NullReferenceException("Unable to retrieve or refresh the cache entry.");
	}

	/// <summary>
	/// Awaits the shared refresh, honouring the caller's <paramref name="cancellationToken"/> for the wait only.
	/// The shared refresh itself is never cancelled - it continues in the background so other callers and the cache still receive the result.
	/// </summary>
	private static async ValueTask<CacheFetchResult<T>?> WaitForRefreshAsync<T>(ValueTask<CacheFetchResult<T>?> refresh, CancellationToken cancellationToken)
	{
		if (!cancellationToken.CanBeCanceled)
		{
			return await refresh.ConfigureAwait(false);
		}

		var refreshTask = refresh.AsTask();
		var cancellationSource = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
		using (cancellationToken.Register(static state => ((TaskCompletionSource<object?>)state!).TrySetResult(null), cancellationSource))
		{
			var completedTask = await Task.WhenAny(refreshTask, cancellationSource.Task).ConfigureAwait(false);
			if (completedTask != refreshTask)
			{
				throw new OperationCanceledException(cancellationToken);
			}
		}

		return await refreshTask.ConfigureAwait(false);
	}

	/// <summary>
	/// Resolves the source receipt for a cache entry served from the layer at <paramref name="layerIndex"/>.
	/// Layers implementing <see cref="ILocalCacheLayer"/> are reported as the memory layer; all others as the fallback layer.
	/// </summary>
	private CacheValueSource GetSource(int layerIndex, bool isStale)
	{
		var isLocalLayer = CacheLayers[layerIndex] is ILocalCacheLayer;
		return (isLocalLayer, isStale) switch
		{
			(true, false) => CacheValueSource.MemoryLayerFresh,
			(true, true) => CacheValueSource.MemoryLayerStale,
			(false, false) => CacheValueSource.FallbackLayerFresh,
			(false, true) => CacheValueSource.FallbackLayerStale
		};
	}

	/// <summary>
	/// Resolves the source receipt for a cache entry served from the layer at <paramref name="layerIndex"/>,
	/// evaluating the entry's freshness against the current time and the given <paramref name="settings"/>.
	/// </summary>
	private CacheValueSource GetSource<T>(int layerIndex, CacheEntry<T> cacheEntry, CacheSettings settings)
	{
		return GetSource(layerIndex, IsStale(cacheEntry, settings));
	}

	/// <summary>
	/// Re-evaluates the freshness of a source receipt for the given <paramref name="cacheEntry"/>,
	/// keeping the layer (or factory) the entry was resolved from.
	/// </summary>
	private static CacheValueSource WithFreshness<T>(CacheValueSource source, CacheEntry<T> cacheEntry, CacheSettings settings)
	{
		var isStale = IsStale(cacheEntry, settings);
		return source switch
		{
			CacheValueSource.MemoryLayerFresh or CacheValueSource.MemoryLayerStale => isStale ? CacheValueSource.MemoryLayerStale : CacheValueSource.MemoryLayerFresh,
			CacheValueSource.FallbackLayerFresh or CacheValueSource.FallbackLayerStale => isStale ? CacheValueSource.FallbackLayerStale : CacheValueSource.FallbackLayerFresh,
			_ => source
		};
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static bool IsStale<T>(CacheEntry<T> cacheEntry, CacheSettings settings)
	{
		var currentTime = DateTimeProvider.Now;
		return cacheEntry.Expiry <= currentTime || cacheEntry.GetStaleDate(settings) < currentTime;
	}

	private async ValueTask BackPopulateCacheAsync<T>(int fromIndexExclusive, string cacheKey, CacheEntry<T> cacheEntry)
	{
		if (KeyLock.AcquireLock(cacheKey))
		{
			var source = GetSource(fromIndexExclusive, isStale: false);
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
				KeyLock.ReleaseLock(cacheKey, cacheEntry, source);
			}
		}
	}

	private async ValueTask<CacheFetchResult<T>?> RefreshValueAsync<T>(string cacheKey, Func<T, Task<T>> asyncValueFactory, CacheSettings settings, CacheEntryStatus entryStatus)
	{
		if (KeyLock.AcquireLock(cacheKey))
		{
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
					var previousSource = GetSource(previousEntryPoint.LayerIndex, previousEntry, settings);
					KeyLock.ReleaseLock(cacheKey, previousEntry, previousSource);
					return new CacheFetchResult<T>(previousEntry.Value, previousSource);
				}

				await using var distributedLock = await Extensions.AwaitAccessAsync(cacheKey).ConfigureAwait(false);

				if (distributedLock.IsLockOwner)
				{
					var oldValue = previousEntry != default ? previousEntry.Value : default;
					var refreshedValue = await asyncValueFactory(oldValue!).ConfigureAwait(false);
					var cacheEntry = new CacheEntry<T>(refreshedValue, settings.TimeToLive);
					var cacheUpdateType = entryStatus switch
					{
						CacheEntryStatus.Miss => CacheUpdateType.AddEntry,
						_ => CacheUpdateType.AddOrUpdateEntry
					};
					await InternalSetAsync(cacheKey, cacheEntry, cacheUpdateType).ConfigureAwait(false);

					KeyLock.ReleaseLock(cacheKey, cacheEntry, CacheValueSource.Factory);
					return new CacheFetchResult<T>(cacheEntry.Value, CacheValueSource.Factory);
				}
				else
				{
					var currentEntryPoint = await GetWithLayerIndexAsync<T>(cacheKey).ConfigureAwait(false);
					var currentEntry = currentEntryPoint.CacheEntry;
					if (currentEntry == default)
					{
						return default;
					}

					return new CacheFetchResult<T>(currentEntry.Value, GetSource(currentEntryPoint.LayerIndex, currentEntry, settings));
				}
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
			var currentEntryPoint = await GetWithLayerIndexAsync<T>(cacheKey).ConfigureAwait(false);
			var currentEntry = currentEntryPoint.CacheEntry;
			if (currentEntry != default && currentEntry.GetStaleDate(settings) > DateTimeProvider.Now)
			{
				var currentSource = GetSource(currentEntryPoint.LayerIndex, currentEntry, settings);
				KeyLock.ReleaseLock(cacheKey, currentEntry, currentSource);
				return new CacheFetchResult<T>(currentEntry.Value, currentSource);
			}

			//Lock until we are notified to be unlocked
			var refreshResult = await KeyLock.WaitAsync(cacheKey).ConfigureAwait(false);
			if (refreshResult.CacheEntry is not CacheEntry<T> refreshedEntry)
			{
				return default;
			}

			//Re-evaluate the freshness of the entry we were handed so the receipt reflects what was actually served.
			return new CacheFetchResult<T>(refreshedEntry.Value, WithFreshness(refreshResult.Source, refreshedEntry, settings));
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
