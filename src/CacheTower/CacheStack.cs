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
	private readonly EvictionVersionTracker EvictionTracker = new();
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

		//Record the flush before touching the layers so an in-flight refresh can avoid resurrecting deleted values
		EvictionTracker.NotifyFlushed();

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

		//Record the eviction before touching the layers so an in-flight refresh can avoid resurrecting the old value
		EvictionTracker.NotifyEvicted(cacheKey);

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
		var receipt = await GetOrSetWithReceiptAsync(cacheKey, valueFactory, settings, CancellationToken.None).ConfigureAwait(false);
		return receipt.Value!;
	}

	/// <summary>
	/// Attempts to retrieve the value for the given <paramref name="cacheKey"/>.
	/// When unavailable, will fallback to use <paramref name="valueFactory"/> to generate the value, storing it in the cache.
	/// </summary>
	/// <remarks>
	/// This is the same read path as <see cref="GetOrSetAsync{T}(string, Func{T, Task{T}}, CacheSettings)"/> but
	/// additionally returns a <see cref="CacheReadReceipt{T}"/> describing where the value came from.
	/// The receipt never changes the value itself.
	/// <para>
	/// When <see cref="CacheSettings.StaleWhileRevalidateWindow"/> is set, an expired entry within that window
	/// is returned with <see cref="CacheValueSource.Stale"/> while a single background refresh is triggered for the key.
	/// </para>
	/// <para>
	/// The <paramref name="cancellationToken"/> only stops this call from waiting - it never cancels the
	/// underlying refresh, which is shared with other callers of the same key.
	/// </para>
	/// </remarks>
	/// <typeparam name="T">The type of value in the cache entry.</typeparam>
	/// <param name="cacheKey">The cache entry's key.</param>
	/// <param name="valueFactory">The value factory called when no cache entry is available.</param>
	/// <param name="settings">Cache control settings.</param>
	/// <param name="cancellationToken">A token that stops this call from waiting for a shared refresh. The shared refresh itself is never cancelled.</param>
	/// <returns>The item from the cache that corresponds to the given <paramref name="cacheKey"/> together with its source receipt.</returns>
	/// <exception cref="ArgumentException">Thrown when <see cref="CacheSettings.StaleWhileRevalidateWindow"/> is negative.</exception>
	public async ValueTask<CacheReadReceipt<T>> GetOrSetWithReceiptAsync<T>(string cacheKey, Func<T, Task<T>> valueFactory, CacheSettings settings, CancellationToken cancellationToken = default)
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

		if (settings.StaleWhileRevalidateWindow.HasValue && settings.StaleWhileRevalidateWindow.Value < TimeSpan.Zero)
		{
			throw new ArgumentException("The stale-while-revalidate window cannot be negative.", nameof(settings));
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
				}
				else if (cacheEntryPoint.LayerIndex > 0)
				{
					//If a lower-level cache is missing the latest data, attempt to set it in the background
					_ = BackPopulateCacheAsync(cacheEntryPoint.LayerIndex, cacheKey, cacheEntry);
				}

				return new CacheReadReceipt<T>(cacheEntry.Value, cacheEntryPoint.LayerIndex == 0 ? CacheValueSource.MemoryLayer : CacheValueSource.FallbackLayer);
			}
			else if (settings.StaleWhileRevalidateWindow.HasValue && cacheEntry.Expiry + settings.StaleWhileRevalidateWindow.Value > currentTime)
			{
				//The entry is expired but still inside the opt-in stale-while-revalidate window:
				//return the expired value and refresh it in the background (single-flight per key).
				_ = RefreshValueAsync(cacheKey, valueFactory, settings, cacheEntryStatus);
				return new CacheReadReceipt<T>(cacheEntry.Value, CacheValueSource.Stale);
			}
			else
			{
				//Refresh the value in the current thread because we only have expired data (expired data is never returned unless opted-in above)
				cacheEntryStatus = CacheEntryStatus.Expired;
			}
		}
		else
		{
			//Refresh the value in the current thread because we have no existing data
			cacheEntryStatus = CacheEntryStatus.Miss;
		}

		var refreshTask = RefreshValueAsync(cacheKey, valueFactory, settings, cacheEntryStatus).AsTask();
		var (refreshedEntry, producedByFactory) = await CancellationUtility.WaitWithCancellationAsync(refreshTask, cancellationToken).ConfigureAwait(false);
		return new CacheReadReceipt<T>(refreshedEntry!.Value, producedByFactory ? CacheValueSource.Factory : CacheValueSource.Fresh);
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

	private async ValueTask<(CacheEntry<T>? CacheEntry, bool ProducedByFactory)> RefreshValueAsync<T>(string cacheKey, Func<T, Task<T>> asyncValueFactory, CacheSettings settings, CacheEntryStatus entryStatus)
	{
		if (KeyLock.AcquireLock(cacheKey))
		{
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
					return (previousEntry, false);
				}

				await using var distributedLock = await Extensions.AwaitAccessAsync(cacheKey).ConfigureAwait(false);

				CacheEntry<T>? cacheEntry;
				var producedByFactory = false;
				if (distributedLock.IsLockOwner)
				{
					var oldValue = previousEntry != default ? previousEntry.Value : default;
					var evictionVersion = EvictionTracker.GetVersion(cacheKey);
					var refreshedValue = await asyncValueFactory(oldValue!).ConfigureAwait(false);
					cacheEntry = new CacheEntry<T>(refreshedValue, settings.TimeToLive);
					if (EvictionTracker.GetVersion(cacheKey) == evictionVersion)
					{
						var cacheUpdateType = entryStatus switch
						{
							CacheEntryStatus.Miss => CacheUpdateType.AddEntry,
							_ => CacheUpdateType.AddOrUpdateEntry
						};
						await InternalSetAsync(cacheKey, cacheEntry, cacheUpdateType).ConfigureAwait(false);
						EvictionTracker.ResetIfUnchanged(cacheKey, evictionVersion);
					}
					else
					{
						//The key was evicted (or the stack flushed) while the value was being refreshed.
						//Skip the write so the deletion is honored and the old value is not resurrected.
						Logger.LogDebug("Skipping cache write for CacheKey={CacheKey} because it was evicted or flushed during the refresh.", cacheKey);
					}

					producedByFactory = true;
					KeyLock.ReleaseLock(cacheKey, cacheEntry);
				}
				else
				{
					cacheEntry = await GetAsync<T>(cacheKey).ConfigureAwait(false);
				}

				return (cacheEntry, producedByFactory);
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
				return (currentEntry, false);
			}

			//Lock until we are notified to be unlocked
			var waitedEntry = await KeyLock.WaitAsync(cacheKey).ConfigureAwait(false) as CacheEntry<T>;
			return (waitedEntry, false);
		}

		return (default, false);
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
