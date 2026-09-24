using System;
using System.Collections.Concurrent;
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
	private readonly ConcurrentDictionary<string, long> EvictionVersions = new(StringComparer.Ordinal);
	private long FlushVersion;
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

		Interlocked.Increment(ref FlushVersion);

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

		RecordEviction(cacheKey);

		await Extensions.OnCacheEvictionAsync(cacheKey).ConfigureAwait(false);
	}

	private void RecordEviction(string cacheKey)
	{
		EvictionVersions.AddOrUpdate(cacheKey, 1, static (_, version) => version + 1);
	}

	private (long Flush, long Eviction) GetInvalidationVersion(string cacheKey)
	{
		EvictionVersions.TryGetValue(cacheKey, out var evictionVersion);
		return (Interlocked.Read(ref FlushVersion), evictionVersion);
	}

	private bool HasBeenInvalidatedSince(string cacheKey, (long Flush, long Eviction) invalidationVersion)
	{
		if (Interlocked.Read(ref FlushVersion) != invalidationVersion.Flush)
		{
			return true;
		}

		return EvictionVersions.TryGetValue(cacheKey, out var evictionVersion) && evictionVersion != invalidationVersion.Eviction;
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
				var cacheEntry = cacheEntryPoint.CacheEntry;
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

				return cacheEntry.Value!;
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

		return (await RefreshValueAsync(cacheKey, valueFactory, settings, cacheEntryStatus).ConfigureAwait(false)).CacheEntry!.Value!;
	}

	/// <inheritdoc/>
	public async ValueTask<CacheReadResult<T>> GetOrSetAsync<T>(string cacheKey, Func<T, Task<T>> valueFactory, CacheSettings settings, StaleWhileRevalidateOptions staleWhileRevalidate, CancellationToken cancellationToken = default)
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
				}
				else if (cacheEntryPoint.LayerIndex > 0)
				{
					//If a lower-level cache is missing the latest data, attempt to set it in the background
					_ = BackPopulateCacheAsync(cacheEntryPoint.LayerIndex, cacheKey, cacheEntry);
				}

				return new CacheReadResult<T>(cacheEntry.Value, new CacheReadReceipt(GetLayerSource(cacheEntryPoint.LayerIndex), CacheValueFreshness.Fresh, cacheEntryPoint.LayerIndex));
			}
			else if (currentTime < cacheEntry.Expiry + staleWhileRevalidate.StaleWindow)
			{
				//The cache entry is expired but still inside the stale-while-revalidate window:
				//serve the stale value and refresh the value once in the background.
				_ = RefreshValueAsync(cacheKey, valueFactory, settings, cacheEntryStatus);

				return new CacheReadResult<T>(cacheEntry.Value, new CacheReadReceipt(GetLayerSource(cacheEntryPoint.LayerIndex), CacheValueFreshness.Stale, cacheEntryPoint.LayerIndex));
			}
			else
			{
				//Refresh the value in the current thread because the existing data is beyond the stale window
				cacheEntryStatus = CacheEntryStatus.Expired;
			}
		}
		else
		{
			//Refresh the value in the current thread because we have no existing data
			cacheEntryStatus = CacheEntryStatus.Miss;
		}

		//The caller's cancellation token only abandons the caller's own wait - the shared refresh itself is never cancelled.
		var refreshTask = RefreshValueAsync(cacheKey, valueFactory, settings, cacheEntryStatus).AsTask();
		var (refreshedEntry, source, layerIndex) = await WaitForCallerAsync(refreshTask, cancellationToken).ConfigureAwait(false);
		return new CacheReadResult<T>(refreshedEntry!.Value, new CacheReadReceipt(source, CacheValueFreshness.Fresh, layerIndex));
	}

	private static async Task<T> WaitForCallerAsync<T>(Task<T> task, CancellationToken cancellationToken)
	{
		if (!cancellationToken.CanBeCanceled || task.IsCompleted)
		{
			return await task.ConfigureAwait(false);
		}

		var cancellationSource = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
		using (cancellationToken.Register(static state => ((TaskCompletionSource<object?>)state!).TrySetResult(null), cancellationSource))
		{
			var completedTask = await Task.WhenAny(task, cancellationSource.Task).ConfigureAwait(false);
			if (completedTask != task)
			{
				throw new OperationCanceledException(cancellationToken);
			}
		}

		return await task.ConfigureAwait(false);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private CacheValueSource GetLayerSource(int layerIndex)
	{
		return CacheLayers[layerIndex] is ILocalCacheLayer ? CacheValueSource.MemoryLayer : CacheValueSource.BackingLayer;
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

	private async ValueTask<(CacheEntry<T>? CacheEntry, CacheValueSource Source, int LayerIndex)> RefreshValueAsync<T>(string cacheKey, Func<T, Task<T>> asyncValueFactory, CacheSettings settings, CacheEntryStatus entryStatus)
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
					KeyLock.ReleaseLock(cacheKey, previousEntry);
					return (previousEntry, GetLayerSource(previousEntryPoint.LayerIndex), previousEntryPoint.LayerIndex);
				}

				await using var distributedLock = await Extensions.AwaitAccessAsync(cacheKey).ConfigureAwait(false);

				CacheEntry<T>? cacheEntry;
				CacheValueSource source;
				var layerIndex = -1;
				if (distributedLock.IsLockOwner)
				{
					var oldValue = previousEntry != default ? previousEntry.Value : default;
					var invalidationVersion = GetInvalidationVersion(cacheKey);
					var refreshedValue = await asyncValueFactory(oldValue!).ConfigureAwait(false);
					cacheEntry = new CacheEntry<T>(refreshedValue, settings.TimeToLive);
					if (HasBeenInvalidatedSince(cacheKey, invalidationVersion))
					{
						//An eviction or flush raced with this refresh - storing the refreshed value would resurrect the invalidated entry.
						Logger.LogDebug("CacheKey={CacheKey} was evicted or flushed during its refresh. The refreshed value will not be stored.", cacheKey);
					}
					else
					{
						var cacheUpdateType = entryStatus switch
						{
							CacheEntryStatus.Miss => CacheUpdateType.AddEntry,
							_ => CacheUpdateType.AddOrUpdateEntry
						};
						await InternalSetAsync(cacheKey, cacheEntry, cacheUpdateType).ConfigureAwait(false);

						//The eviction bookkeeping for this key was not invalidated by this refresh and can be discarded.
						((ICollection<KeyValuePair<string, long>>)EvictionVersions).Remove(new KeyValuePair<string, long>(cacheKey, invalidationVersion.Eviction));
					}

					source = CacheValueSource.Factory;
					KeyLock.ReleaseLock(cacheKey, cacheEntry);
				}
				else
				{
					var cacheEntryPoint = await GetWithLayerIndexAsync<T>(cacheKey).ConfigureAwait(false);
					cacheEntry = cacheEntryPoint != default ? cacheEntryPoint.CacheEntry : default;
					if (cacheEntryPoint != default)
					{
						source = GetLayerSource(cacheEntryPoint.LayerIndex);
						layerIndex = cacheEntryPoint.LayerIndex;
					}
					else
					{
						source = CacheValueSource.Factory;
					}
				}

				return (cacheEntry, source, layerIndex);
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
				KeyLock.ReleaseLock(cacheKey, currentEntryPoint.CacheEntry);
				return (currentEntryPoint.CacheEntry, GetLayerSource(currentEntryPoint.LayerIndex), currentEntryPoint.LayerIndex);
			}

			//Lock until we are notified to be unlocked
			var awaitedEntry = await KeyLock.WaitAsync(cacheKey).ConfigureAwait(false) as CacheEntry<T>;
			//The entry was released by the caller that owned the refresh - attribute it to the layer that now serves it when possible.
			var awaitedEntryPoint = await GetWithLayerIndexAsync<T>(cacheKey).ConfigureAwait(false);
			if (awaitedEntryPoint != default)
			{
				return (awaitedEntry, GetLayerSource(awaitedEntryPoint.LayerIndex), awaitedEntryPoint.LayerIndex);
			}

			return (awaitedEntry, CacheValueSource.Factory, -1);
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
