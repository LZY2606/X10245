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

		await KeyLock.EvictCoordinatedAsync(cacheKey, async () =>
		{
			for (int i = 0, l = CacheLayers.Length; i < l; i++)
			{
				var layer = CacheLayers[i];
				await layer.EvictAsync(cacheKey).ConfigureAwait(false);
			}
		}).ConfigureAwait(false);

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
		var readResult = await GetOrSetCoreAsync(cacheKey, valueFactory, settings, CancellationToken.None).ConfigureAwait(false);
		return readResult.Value!;
	}

	/// <inheritdoc/>
	public async ValueTask<CacheReadResult<T>> GetOrSetAsync<T>(string cacheKey, Func<T, Task<T>> valueFactory, CacheSettings settings, CancellationToken cancellationToken)
	{
		var readResult = await GetOrSetCoreAsync(cacheKey, valueFactory, settings, cancellationToken).ConfigureAwait(false);
		return new CacheReadResult<T>(readResult.Value!, readResult.Receipt);
	}

	private async ValueTask<(T? Value, CacheReadReceipt Receipt)> GetOrSetCoreAsync<T>(
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

		var currentTime = DateTimeProvider.Now;
		var cacheEntryPoint = await GetWithLayerIndexAsync<T>(cacheKey).ConfigureAwait(false);
		var cacheEntryStatus = CacheEntryStatus.Stale;
		if (cacheEntryPoint != default)
		{
			var cacheEntry = cacheEntryPoint.CacheEntry;
			if (cacheEntry.Expiry > currentTime)
			{
				//Unexpired entry
				var staleDate = cacheEntry.GetStaleDate(settings);
				if (settings.StaleAfter.HasValue && staleDate < currentTime)
				{
					//If the cache entry is stale, refresh the value in the background
					_ = SafeBackgroundRefreshAsync(cacheKey, valueFactory, settings, CacheEntryStatus.Stale);
					return (cacheEntry.Value, CacheReadReceipt.StaleFromLayer(cacheEntryPoint.LayerIndex));
				}
				else if (cacheEntryPoint.LayerIndex > 0)
				{
					//If a lower-level cache is missing the latest data, attempt to set it in the background
					_ = BackPopulateCacheAsync(cacheEntryPoint.LayerIndex, cacheKey, cacheEntry);
				}

				return (cacheEntry.Value, CacheReadReceipt.FreshFromLayer(cacheEntryPoint.LayerIndex));
			}
			else if (settings.StaleWhileRevalidate.HasValue && cacheEntry.Expiry + settings.StaleWhileRevalidate.Value > currentTime)
			{
				//Opt-in stale-while-revalidate: serve the expired value within the window and trigger a single background refresh.
				_ = SafeBackgroundRefreshAsync(cacheKey, valueFactory, settings, CacheEntryStatus.Stale);
				return (cacheEntry.Value, CacheReadReceipt.StaleFromLayer(cacheEntryPoint.LayerIndex));
			}
			else
			{
				//Refresh the value in the current thread because the expired data can no longer be served
				cacheEntryStatus = CacheEntryStatus.Expired;
			}
		}
		else
		{
			//Refresh the value in the current thread because we have no existing data
			cacheEntryStatus = CacheEntryStatus.Miss;
		}

		var refreshed = await RefreshValueAsync(cacheKey, valueFactory, settings, cacheEntryStatus, cancellationToken).ConfigureAwait(false);
		return (refreshed.CacheEntry!.Value, refreshed.Receipt);
	}

	/// <summary>
	/// Runs a background refresh without propagating its exception to a caller that already received a value.
	/// A failed background refresh never mutates cache state and therefore never extends the stale window.
	/// </summary>
	private async Task SafeBackgroundRefreshAsync<T>(string cacheKey, Func<T, Task<T>> valueFactory, CacheSettings settings, CacheEntryStatus entryStatus)
	{
		try
		{
			await RefreshValueAsync(cacheKey, valueFactory, settings, entryStatus, CancellationToken.None).ConfigureAwait(false);
		}
		catch (Exception e)
		{
			Logger.LogWarning(e, "Background refresh failed for CacheKey={CacheKey}. The stale window was not extended.", cacheKey);
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

	private async ValueTask<(CacheEntry<T>? CacheEntry, CacheReadReceipt Receipt)> RefreshValueAsync<T>(
		string cacheKey,
		Func<T, Task<T>> asyncValueFactory,
		CacheSettings settings,
		CacheEntryStatus entryStatus,
		CancellationToken cancellationToken)
	{
		var evictionGeneration = KeyLock.CaptureEvictionGeneration(cacheKey);

		if (KeyLock.AcquireLock(cacheKey))
		{
			try
			{
				var previousEntry = await GetWithLayerIndexAsync<T>(cacheKey).ConfigureAwait(false);
				if (previousEntry != default && entryStatus == CacheEntryStatus.Miss && previousEntry.CacheEntry.Expiry >= DateTimeProvider.Now)
				{
					//The Cache Stack will always return an unexpired value if one exists.
					//If we are told to refresh because one doesn't and we find one, we return the existing value, ignoring the refresh.
					//This can happen due to the race condition of getting the values out of the cache.
					//We can only do any of this because we have the local lock.
					KeyLock.ReleaseLock(cacheKey, previousEntry.CacheEntry);
					return (previousEntry.CacheEntry, CacheReadReceipt.FreshFromLayer(previousEntry.LayerIndex));
				}

				await using var distributedLock = await Extensions.AwaitAccessAsync(cacheKey).ConfigureAwait(false);

				CacheEntry<T>? cacheEntry;
				CacheReadReceipt receipt;
				if (distributedLock.IsLockOwner)
				{
					var oldValue = previousEntry != default ? previousEntry.CacheEntry.Value : default;
					//Cancellation is deliberately not forwarded to the factory: a caller abandoning a shared refresh must not cancel it for everyone.
					var refreshedValue = await asyncValueFactory(oldValue!).ConfigureAwait(false);
					cacheEntry = new CacheEntry<T>(refreshedValue, settings.TimeToLive);

					if (KeyLock.WasEvictedSince(cacheKey, evictionGeneration))
					{
						//The key was evicted while the refresh was running. The refresh must not resurrect the old or new value.
						KeyLock.ReleaseLock(cacheKey, cacheEntry);
						Logger.LogInformation("Discarding refresh for CacheKey={CacheKey} as it was evicted concurrently.", cacheKey);
					}
					else
					{
						var cacheUpdateType = entryStatus switch
						{
							CacheEntryStatus.Miss => CacheUpdateType.AddEntry,
							_ => CacheUpdateType.AddOrUpdateEntry
						};
						await InternalSetAsync(cacheKey, cacheEntry, cacheUpdateType).ConfigureAwait(false);
						KeyLock.ReleaseLock(cacheKey, cacheEntry);
					}

					receipt = CacheReadReceipt.FromFactory();
				}
				else
				{
					var reReadEntry = await GetWithLayerIndexAsync<T>(cacheKey).ConfigureAwait(false);
					cacheEntry = reReadEntry != default ? reReadEntry.CacheEntry : null;
					receipt = reReadEntry != default
						? CacheReadReceipt.FreshFromLayer(reReadEntry.LayerIndex)
						: CacheReadReceipt.FromFactory();
				}

				return (cacheEntry, receipt);
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
				var currentEntry = currentEntryPoint.CacheEntry;
				KeyLock.ReleaseLock(cacheKey, currentEntry);
				return (currentEntry, CacheReadReceipt.FreshFromLayer(currentEntryPoint.LayerIndex));
			}

			//Lock until we are notified to be unlocked. Waiting honors the caller's token but the shared refresh itself is never cancelled.
			var waitedEntry = await KeyLock.WaitAsync(cacheKey).WithCancellation(cancellationToken).ConfigureAwait(false);
			if (waitedEntry is CacheEntryKeyLock.EvictionSentinel)
			{
				//An eviction barrier completed while we were waiting - re-evaluate as a miss and register a fresh refresh.
				return await RefreshValueAsync(cacheKey, asyncValueFactory, settings, entryStatus, cancellationToken).ConfigureAwait(false);
			}

			if (waitedEntry is CacheEntry<T> cachedEntry)
			{
				return (cachedEntry, CacheReadReceipt.FreshFromLayer(0));
			}

			//Defensive: an unknown published state is treated as unsupported rather than guessing success.
			throw new InvalidOperationException($"Received an unexpected cache wait result of type {waitedEntry?.GetType().FullName ?? "null"}.");
		}

		return (default, CacheReadReceipt.FromFactory());
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
