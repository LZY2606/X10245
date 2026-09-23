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

		//Mark the eviction before touching any layers so an in-flight refresh cannot repopulate a key that was deleted.
		KeyLock.MarkEvicted(cacheKey);

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
		await InternalSetAsync(cacheKey, cacheEntry, CacheUpdateType.AddOrUpdateEntry, evictionGeneration: null).ConfigureAwait(false);
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

		await InternalSetAsync(cacheKey, cacheEntry, CacheUpdateType.AddOrUpdateEntry, evictionGeneration: null).ConfigureAwait(false);
	}

	private async ValueTask InternalSetAsync<T>(string cacheKey, CacheEntry<T> cacheEntry, CacheUpdateType cacheUpdateType, long? evictionGeneration)
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

		//If the key was evicted while we were writing, undo the resurrection so a delete cannot race with a refresh to bring an old value back.
		if (evictionGeneration.HasValue && KeyLock.WasEvictedSince(cacheKey, evictionGeneration.Value))
		{
			for (int i = 0, l = CacheLayers.Length; i < l; i++)
			{
				await CacheLayers[i].EvictAsync(cacheKey).ConfigureAwait(false);
			}
			return;
		}

		if (evictionGeneration.HasValue)
		{
			KeyLock.ClearActive(cacheKey, evictionGeneration.Value);
		}

		await Extensions.OnCacheUpdateAsync(cacheKey, cacheEntry.Expiry, cacheUpdateType).ConfigureAwait(false);
	}

	/// <inheritdoc/>
	public ValueTask<CacheEntry<T>?> GetAsync<T>(string cacheKey)
	{
		ThrowIfDisposed();

		if (cacheKey == null)
		{
			throw new ArgumentNullException(nameof(cacheKey));
		}

		return GetCoreAsync<T>(cacheKey);
	}

	private async ValueTask<CacheEntry<T>?> GetCoreAsync<T>(string cacheKey)
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
	public ValueTask<T> GetOrSetAsync<T>(string cacheKey, Func<T, Task<T>> valueFactory, CacheSettings settings)
		=> GetOrSetAsync(cacheKey, valueFactory, settings, default);

	/// <inheritdoc/>
	public async ValueTask<T> GetOrSetAsync<T>(string cacheKey, Func<T, Task<T>> valueFactory, CacheSettings settings, CacheAccessOptions accessOptions)
	{
		var result = await GetOrSetWithReceiptAsync(cacheKey, valueFactory, settings, accessOptions).ConfigureAwait(false);
		return result.Value!;
	}

	/// <inheritdoc/>
	public ValueTask<CacheAccessResult<T>> GetOrSetWithReceiptAsync<T>(string cacheKey, Func<T, Task<T>> valueFactory, CacheSettings settings)
		=> GetOrSetWithReceiptAsync(cacheKey, valueFactory, settings, default);

	/// <inheritdoc/>
	public async ValueTask<CacheAccessResult<T>> GetOrSetWithReceiptAsync<T>(string cacheKey, Func<T, Task<T>> valueFactory, CacheSettings settings, CacheAccessOptions accessOptions)
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

		var staleWhileRevalidate = accessOptions.StaleWhileRevalidate ?? settings.StaleWhileRevalidate;

		//Only one foreground attempt is allowed after a delete/refresh race so we converge rather than loop forever.
		const int maxForegroundAttempts = 2;
		for (var attempt = 0; attempt < maxForegroundAttempts; attempt++)
		{
			var currentTime = DateTimeProvider.Now;
			var cacheEntryPoint = await GetWithLayerIndexAsync<T>(cacheKey).ConfigureAwait(false);
			if (cacheEntryPoint != default)
			{
				var cacheEntry = cacheEntryPoint.CacheEntry;
				if (cacheEntry.Expiry > currentTime)
				{
					if (settings.StaleAfter.HasValue && cacheEntry.GetStaleDate(settings) <= currentTime)
					{
						//If the cache entry is stale, refresh the value in the background
						var backgrounded = TryQueueBackgroundRefresh(cacheKey, valueFactory, settings);
						return new(cacheEntry.Value, CacheReadReceipt.FromLayer(cacheEntryPoint.LayerIndex, CacheDataFreshness.Stale, cacheEntry.Expiry, backgrounded));
					}
					else if (cacheEntryPoint.LayerIndex > 0)
					{
						//If a lower-level cache is missing the latest data, attempt to set it in the background
						_ = BackPopulateCacheAsync(cacheEntryPoint.LayerIndex, cacheKey, cacheEntry);
					}

					return new(cacheEntry.Value, CacheReadReceipt.FromLayer(cacheEntryPoint.LayerIndex, CacheDataFreshness.Fresh, cacheEntry.Expiry, false));
				}
				else if (staleWhileRevalidate.HasValue && cacheEntry.Expiry + staleWhileRevalidate.Value > currentTime)
				{
					//Opt-in stale-while-revalidate: serve the expired value but trigger a single background refresh.
					var backgrounded = TryQueueBackgroundRefresh(cacheKey, valueFactory, settings);
					return new(cacheEntry.Value, CacheReadReceipt.FromLayer(cacheEntryPoint.LayerIndex, CacheDataFreshness.ExpiredButServed, cacheEntry.Expiry, backgrounded));
				}
				else
				{
					//Expired data that cannot be served - refresh in the foreground.
					var outcome = await RefreshValueAsync(cacheKey, valueFactory, settings, CacheEntryStatus.Expired, accessOptions.CancellationToken).ConfigureAwait(false);
					if (outcome.CacheEntry == null)
					{
						continue;
					}
					return new(outcome.CacheEntry.Value, outcome.Receipt);
				}
			}
			else
			{
				//No existing data - refresh in the foreground.
				var outcome = await RefreshValueAsync(cacheKey, valueFactory, settings, CacheEntryStatus.Miss, accessOptions.CancellationToken).ConfigureAwait(false);
				if (outcome.CacheEntry == null)
				{
					continue;
				}
				return new(outcome.CacheEntry.Value, outcome.Receipt);
			}
		}

		//This should be unreachable - returning a miss would mean guessing success which we never do.
		throw new InvalidOperationException("Failed to resolve a cache value after a concurrent eviction retry for key '" + cacheKey + "'.");
	}

	private bool TryQueueBackgroundRefresh<T>(string cacheKey, Func<T, Task<T>> valueFactory, CacheSettings settings)
	{
		if (KeyLock.AcquireLock(cacheKey, out _))
		{
			_ = RunBackgroundRefreshAsync(cacheKey, valueFactory, settings);
			return true;
		}

		return false;
	}

	private async Task RunBackgroundRefreshAsync<T>(string cacheKey, Func<T, Task<T>> valueFactory, CacheSettings settings)
	{
		try
		{
			await RefreshValueAsync(cacheKey, valueFactory, settings, CacheEntryStatus.Stale, default).ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			//A background refresh cannot be observed by callers (they already received their value) so log instead of crashing.
			Logger.LogWarning(ex, "Background cache refresh failed for CacheKey={CacheKey}. The stale window is not extended by the failure.", cacheKey);
		}
	}
