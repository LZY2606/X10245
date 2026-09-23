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

		//Coordinate with any in-flight refresh for the key so a completing refresh cannot resurrect the evicted value.
		KeyLock.NotifyEviction(cacheKey);
		while (KeyLock.WaitForCompletionAsync(cacheKey) is { } inFlightRefresh)
		{
			try
			{
				await inFlightRefresh.ConfigureAwait(false);
			}
			catch
			{
				//A failed refresh does not block eviction - the eviction still proceeds.
			}
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
		var result = await GetOrSetAsync(cacheKey, valueFactory, settings, CancellationToken.None).ConfigureAwait(false);
		return result.Value!;
	}

	/// <summary>
	/// Attempts to retrieve the value for the given <paramref name="cacheKey"/>, along with a <see cref="CacheReadReceipt"/> describing where the value came from.
	/// When unavailable, will fallback to use <paramref name="valueFactory"/> to generate the value, storing it in the cache.
	/// </summary>
	/// <remarks>
	/// This overload additionally honours the opt-in <see cref="CacheSettings.StaleWhileRevalidateWindow"/>:
	/// an expired entry inside that window is served (marked stale on the receipt) while a single background refresh runs per key.
	/// <para>
	/// Cancelling <paramref name="cancellationToken"/> only cancels this caller's wait - it never cancels the shared refresh, which still completes and populates the cache.
	/// </para>
	/// </remarks>
	/// <typeparam name="T">The type of value in the cache entry.</typeparam>
	/// <param name="cacheKey">The cache entry's key.</param>
	/// <param name="valueFactory">The value factory called when no cache entry is available.</param>
	/// <param name="settings">Cache control settings.</param>
	/// <param name="cancellationToken">A token to cancel this caller's wait for the value. The shared refresh is not cancelled.</param>
	/// <returns>The <see cref="CacheReadResult{T}"/> for the given <paramref name="cacheKey"/>.</returns>
	public async ValueTask<CacheReadResult<T>> GetOrSetAsync<T>(string cacheKey, Func<T, Task<T>> valueFactory, CacheSettings settings, CancellationToken cancellationToken)
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

		//The shared refresh never observes the caller's cancellation token, so it always runs to completion.
		var readTask = GetOrSetCoreAsync(cacheKey, valueFactory, settings).AsTask();
		if (!cancellationToken.CanBeCanceled || readTask.IsCompleted)
		{
			return await readTask.ConfigureAwait(false);
		}

		var completedTask = await Task.WhenAny(readTask, WaitForCancellationAsync(cancellationToken)).ConfigureAwait(false);
		if (completedTask != readTask)
		{
			throw new OperationCanceledException(cancellationToken);
		}
		return await readTask.ConfigureAwait(false);
	}

	private static async Task WaitForCancellationAsync(CancellationToken cancellationToken)
	{
		var completionSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		using (cancellationToken.Register(() => completionSource.TrySetResult(true)))
		{
			await completionSource.Task.ConfigureAwait(false);
		}
	}

	private async ValueTask<CacheReadResult<T>> GetOrSetCoreAsync<T>(string cacheKey, Func<T, Task<T>> valueFactory, CacheSettings settings)
	{
		var currentTime = DateTimeProvider.Now;
		var cacheEntryPoint = await GetWithLayerIndexAsync<T>(cacheKey).ConfigureAwait(false);
		var cacheEntryStatus = CacheEntryStatus.Stale;
		if (cacheEntryPoint != default)
		{
			var cacheEntry = cacheEntryPoint.CacheEntry;
			var layerSource = GetSourceForLayer(cacheEntryPoint.LayerIndex);
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

				return CreateReadResult(cacheEntry, layerSource, settings, currentTime);
			}
			else if (settings.StaleWhileRevalidateWindow.HasValue && cacheEntry.Expiry + settings.StaleWhileRevalidateWindow.Value >= currentTime)
			{
				//Opt-in stale-while-revalidate: serve the expired entry within the window while a single background refresh runs per key.
				//The window is measured from the entry's own expiry, so a failed refresh never extends it.
				_ = RefreshValueAsync(cacheKey, valueFactory, settings, CacheEntryStatus.Stale);
				return CreateReadResult(cacheEntry, layerSource, settings, currentTime);
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

		var refreshOutcome = await RefreshValueAsync(cacheKey, valueFactory, settings, cacheEntryStatus).ConfigureAwait(false);
		if (refreshOutcome is null)
		{
			return new CacheReadResult<T>(default, new CacheReadReceipt(CacheReadSource.Unknown, false));
		}
		return CreateReadResult(refreshOutcome.Value.CacheEntry, refreshOutcome.Value.Source, settings, DateTimeProvider.Now);
	}

	private static CacheReadSource GetSourceForLayer(int layerIndex)
	{
		return layerIndex == 0 ? CacheReadSource.MemoryLayer : CacheReadSource.FallbackLayer;
	}

	private static CacheReadResult<T> CreateReadResult<T>(CacheEntry<T> cacheEntry, CacheReadSource source, CacheSettings settings, DateTime currentTime)
	{
		var isStale = cacheEntry.GetStaleDate(settings) < currentTime;
		return new CacheReadResult<T>(cacheEntry.Value, new CacheReadReceipt(source, isStale));
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
				KeyLock.ReleaseLock(cacheKey, cacheEntry, CacheReadSource.FallbackLayer);
			}
		}
	}

	private async ValueTask<(CacheEntry<T> CacheEntry, CacheReadSource Source)?> RefreshValueAsync<T>(string cacheKey, Func<T, Task<T>> asyncValueFactory, CacheSettings settings, CacheEntryStatus entryStatus)
	{
		if (KeyLock.AcquireLock(cacheKey))
		{
			try
			{
				var previousEntryPoint = await GetWithLayerIndexAsync<T>(cacheKey).ConfigureAwait(false);
				var previousEntry = previousEntryPoint != default ? previousEntryPoint.CacheEntry : null;
				if (previousEntry != default && entryStatus == CacheEntryStatus.Miss && previousEntry.Expiry >= DateTimeProvider.Now)
				{
					//The Cache Stack will always return an unexpired value if one exists.
					//If we are told to refresh because one doesn't and we find one, we return the existing value, ignoring the refresh.
					//This can happen due to the race condition of getting the values out of the cache.
					//We can only do any of this because we have the local lock.
					var existingSource = GetSourceForLayer(previousEntryPoint.LayerIndex);
					KeyLock.ReleaseLock(cacheKey, previousEntry, existingSource);
					return (previousEntry, existingSource);
				}

				await using var distributedLock = await Extensions.AwaitAccessAsync(cacheKey).ConfigureAwait(false);

				if (distributedLock.IsLockOwner)
				{
					var oldValue = previousEntry != default ? previousEntry.Value : default;
					var refreshedValue = await asyncValueFactory(oldValue!).ConfigureAwait(false);
					var cacheEntry = new CacheEntry<T>(refreshedValue, settings.TimeToLive);
					if (!KeyLock.ConsumeEviction(cacheKey))
					{
						var cacheUpdateType = entryStatus switch
						{
							CacheEntryStatus.Miss => CacheUpdateType.AddEntry,
							_ => CacheUpdateType.AddOrUpdateEntry
						};
						await InternalSetAsync(cacheKey, cacheEntry, cacheUpdateType).ConfigureAwait(false);
					}
					//else: the key was evicted while the refresh was in flight - do not resurrect the value into the cache.

					KeyLock.ReleaseLock(cacheKey, cacheEntry, CacheReadSource.Factory);
					return (cacheEntry, CacheReadSource.Factory);
				}
				else
				{
					var cacheEntry = await GetAsync<T>(cacheKey).ConfigureAwait(false);
					return cacheEntry != default ? (cacheEntry, CacheReadSource.Unknown) : null;
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
			if (currentEntryPoint != default && currentEntryPoint.CacheEntry.GetStaleDate(settings) > DateTimeProvider.Now)
			{
				var currentSource = GetSourceForLayer(currentEntryPoint.LayerIndex);
				KeyLock.ReleaseLock(cacheKey, currentEntryPoint.CacheEntry, currentSource);
				return (currentEntryPoint.CacheEntry, currentSource);
			}

			//Lock until we are notified to be unlocked
			var notification = await KeyLock.WaitAsync(cacheKey).ConfigureAwait(false);
			return ((CacheEntry<T>)notification.CacheEntry, notification.Source);
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
