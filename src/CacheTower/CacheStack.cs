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
/// A <see cref="CacheStack"/> is the backbone of caching for Cache Tower. It manages coordination between the various cache layers, manages the cache extensions and handles background refreshing.
/// </summary>
public class CacheStack : ICacheStack, IFlushableCacheStack, IExtendableCacheStack, IAsyncDisposable
{
	private bool Disposed;

	private readonly CacheEntryKeyLock KeyLock = new();
	private readonly ILogger<CacheStack> Logger;
	private readonly ICacheLayer[] CacheLayers;
	private readonly ExtensionContainer Extensions;

	// Tracks evictions/flushes so a refresh that races an EvictAsync/FlushAsync cannot resurrect the old value.
	private readonly object EvictionLock = new();
	private long evictionGeneration;
	private readonly Dictionary<string, long> EvictionVersions = new(StringComparer.Ordinal);

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

	/// <summary>
	/// Captures the current eviction state for a key. Used to detect an eviction/flush that races a refresh.
	/// </summary>
	private readonly record struct EvictionState(long Generation, long KeyVersion)
	{
		public bool IsCurrent(EvictionState other) => Generation == other.Generation && KeyVersion == other.KeyVersion;
	}

	private EvictionState CaptureEvictionState(string cacheKey)
	{
		lock (EvictionLock)
		{
			EvictionVersions.TryGetValue(cacheKey, out var keyVersion);
			return new EvictionState(evictionGeneration, keyVersion);
		}
	}

	private EvictionState CurrentEvictionState(string cacheKey) => CaptureEvictionState(cacheKey);

	/// <summary>
	/// The number of keys with an in-flight refresh or waiting callers. Internal diagnostic for verifying single-flight cleanup.
	/// </summary>
	internal int ActiveRefreshCount => KeyLock.ActiveRefreshCount;

	/// <inheritdoc/>
	public async ValueTask FlushAsync()
	{
		ThrowIfDisposed();

		lock (EvictionLock)
		{
			evictionGeneration++;
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

		lock (EvictionLock)
		{
			EvictionVersions.TryGetValue(cacheKey, out var current);
			EvictionVersions[cacheKey] = current + 1;
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

	/// <summary>
	/// Writes a refresh result to the layers only when no eviction/flush happened while the factory was running.
	/// This prevents a refresh completing after an eviction from resurrecting the freshly produced value as "cached".
	/// </summary>
	private async ValueTask<bool> InternalSetIfNotEvictedAsync<T>(string cacheKey, CacheEntry<T> cacheEntry, CacheUpdateType cacheUpdateType, EvictionState evictionState)
	{
		var wasEvicted = !evictionState.IsCurrent(CurrentEvictionState(cacheKey));
		if (wasEvicted)
		{
			Logger.LogDebug("Skipping cache update for CacheKey={CacheKey} as an eviction occurred during refresh.", cacheKey);
			return false;
		}

		for (int i = 0, l = CacheLayers.Length; i < l; i++)
		{
			var layer = CacheLayers[i];

			//Re-check before each layer write so an eviction racing the multi-layer update cannot partially resurrect the value.
			if (!evictionState.IsCurrent(CurrentEvictionState(cacheKey)))
			{
				Logger.LogDebug("Aborting cache update for Cache={CacheKey} as an eviction occurred mid-update.", cacheKey);
				return false;
			}

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
		return true;
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

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static CacheValueSource LayerSource(int layerIndex) => layerIndex == 0 ? CacheValueSource.MemoryLayer : CacheValueSource.BackingLayer;

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static CacheReadResult<T> CachedResult<T>(CacheEntry<T> cacheEntry, int layerIndex, CacheValueFreshness freshness)
		=> new(cacheEntry.Value, LayerSource(layerIndex), freshness, layerIndex);

	/// <inheritdoc/>
	public ValueTask<T> GetOrSetAsync<T>(string cacheKey, Func<T, Task<T>> valueFactory, CacheSettings settings)
		=> GetOrSetAsync(cacheKey, valueFactory, settings, CancellationToken.None);

	/// <inheritdoc/>
	public async ValueTask<T> GetOrSetAsync<T>(string cacheKey, Func<T, Task<T>> valueFactory, CacheSettings settings, CancellationToken cancellationToken)
	{
		var result = await GetOrSetCoreAsync(cacheKey, valueFactory, settings, CacheReadPolicy.Default, cancellationToken).ConfigureAwait(false);
		return result.Value!;
	}

	/// <inheritdoc/>
	public ValueTask<CacheReadResult<T>> GetOrSetWithReceiptAsync<T>(string cacheKey, Func<T, Task<T>> valueFactory, CacheSettings settings, CacheReadPolicy readPolicy = CacheReadPolicy.Default, CancellationToken cancellationToken = default)
		=> GetOrSetCoreAsync(cacheKey, valueFactory, settings, readPolicy, cancellationToken);

	private async ValueTask<CacheReadResult<T>> GetOrSetCoreAsync<T>(string cacheKey, Func<T, Task<T>> valueFactory, CacheSettings settings, CacheReadPolicy readPolicy, CancellationToken cancellationToken)
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

		if (readPolicy is not (CacheReadPolicy.Default or CacheReadPolicy.StaleWhileRevalidate))
		{
			throw new ArgumentOutOfRangeException(nameof(readPolicy), (int)readPolicy, "Unknown cache read policy.");
		}

		cancellationToken.ThrowIfCancellationRequested();

		var currentTime = DateTimeProvider.Now;
		var cacheEntryPoint = await GetWithLayerIndexAsync<T>(cacheKey).ConfigureAwait(false);
		if (cacheEntryPoint != default)
		{
			var cacheEntry = cacheEntryPoint.CacheEntry;
			if (cacheEntry.Expiry > currentTime)
			{
				if (settings.StaleAfter.HasValue && cacheEntry.GetStaleDate(settings) < currentTime)
				{
					//If the cache entry is stale (but not expired), refresh the value in the background
					TrackBackgroundRefresh(RefreshValueAsync(cacheKey, valueFactory, settings, CacheEntryStatus.Stale, CancellationToken.None));
				}
				else if (cacheEntryPoint.LayerIndex > 0)
				{
					//If a lower-level cache is missing the latest data, attempt to set it in the background
					_ = BackPopulateCacheAsync(cacheEntryPoint.LayerIndex, cacheKey, cacheEntry);
				}

				return CachedResult(cacheEntry, cacheEntryPoint.LayerIndex, CacheValueFreshness.Fresh);
			}

			//Expired entry: serve it once under stale-while-revalidate if it is still inside the window
			if (readPolicy == CacheReadPolicy.StaleWhileRevalidate && settings.StaleWhileRevalidate is TimeSpan swrWindow && currentTime <= cacheEntry.Expiry + swrWindow)
			{
				TrackBackgroundRefresh(RefreshValueAsync(cacheKey, valueFactory, settings, CacheEntryStatus.Stale, CancellationToken.None));
				return CachedResult(cacheEntry, cacheEntryPoint.LayerIndex, CacheValueFreshness.Stale);
			}

			//Refresh the value in the current thread because we only have expired data (we never return expired data by default)
			return await RefreshValueAsync(cacheKey, valueFactory, settings, CacheEntryStatus.Expired, cancellationToken).ConfigureAwait(false);
		}

		//Refresh the value in the current thread because we have no existing data
		return await RefreshValueAsync(cacheKey, valueFactory, settings, CacheEntryStatus.Miss, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// Observes failures of fire-and-forget background refreshes so they are logged with stable diagnostics instead of going unobserved.
	/// </summary>
	private async void TrackBackgroundRefresh<T>(ValueTask<CacheReadResult<T>?> refresh)
	{
		try
		{
			await refresh.ConfigureAwait(false);
		}
		catch (Exception e)
		{
			Logger.LogError(e, "Background cache refresh failed.");
		}
	}

	private async ValueTask BackPopulateCacheAsync<T>(int fromIndexExclusive, string cacheKey, CacheEntry<T> cacheEntry)
	{
		if (KeyLock.AcquireLock(cacheKey))
		{
			var evictionState = CaptureEvictionState(cacheKey);
			try
			{
				for (; --fromIndexExclusive >= 0;)
				{
					if (!evictionState.IsCurrent(CurrentEvictionState(cacheKey)))
					{
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
				KeyLock.ReleaseLock(cacheKey, new RefreshResultEntry<T>(cacheEntry, false));
			}
		}
	}

	/// <summary>
	/// Performs or joins the single shared refresh for <paramref name="cacheKey"/>.
	/// </summary>
	/// <remarks>
	/// The caller's <paramref name="cancellationToken"/> can unblock a waiting read but is never forwarded to the value factory:
	/// cancelling one caller never cancels a refresh shared with other reads.
	/// </remarks>
	private async ValueTask<CacheReadResult<T>?> RefreshValueAsync<T>(string cacheKey, Func<T, Task<T>> asyncValueFactory, CacheSettings settings, CacheEntryStatus entryStatus, CancellationToken cancellationToken)
	{
		if (KeyLock.AcquireLock(cacheKey))
		{
			//Snapshot the eviction state before invoking the factory so a delete/flush racing the refresh cannot resurrect the produced value.
			var evictionState = CaptureEvictionState(cacheKey);
			try
			{
				var previousEntry = await GetAsync<T>(cacheKey).ConfigureAwait(false);
				if (previousEntry != default && entryStatus == CacheEntryStatus.Miss && previousEntry.Expiry >= DateTimeProvider.Now)
				{
					//The Cache Stack will always return an unexpired value if one exists.
					//If we are told to refresh because one doesn't and we find one, we return the existing value, ignoring the refresh.
					//This can happen due to the race condition of getting the values out of the cache.
					//We can only do any of this because we have the local lock.
					KeyLock.ReleaseLock(cacheKey, new RefreshResultEntry<T>(previousEntry, false));
					return CachedResult(previousEntry, 0, CacheValueFreshness.Fresh);
				}

				await using var distributedLock = await Extensions.AwaitAccessAsync(cacheKey).ConfigureAwait(false);

				CacheEntry<T> cacheEntry;
				var servedFromFactory = false;
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
					_ = await InternalSetIfNotEvictedAsync(cacheKey, cacheEntry, cacheUpdateType, evictionState).ConfigureAwait(false);

					servedFromFactory = true;
				}
				else
				{
					var refreshedEntry = await GetAsync<T>(cacheKey).ConfigureAwait(false);
					if (refreshedEntry is null)
					{
						//We do not own the distributed lock and cannot see the value another system produced - release the local lock without a result.
						KeyLock.ReleaseLock(cacheKey, new RefreshResultEntry<T>(null!, false));
						return default;
					}
					cacheEntry = refreshedEntry;
				}

				KeyLock.ReleaseLock(cacheKey, new RefreshResultEntry<T>(cacheEntry, servedFromFactory));

				cancellationToken.ThrowIfCancellationRequested();
				return new CacheReadResult<T>(cacheEntry.Value, CacheValueSource.Factory, CacheValueFreshness.Fresh, null);
			}
			catch (Exception e)
			{
				KeyLock.ReleaseLock(cacheKey, e);

				//A cancelled caller is detached: the shared refresh has already completed and released the lock for other waiters.
				if (cancellationToken.IsCancellationRequested && e is not OperationCanceledException)
				{
					throw new OperationCanceledException(cancellationToken);
				}

				throw;
			}
		}
		else if (entryStatus != CacheEntryStatus.Stale)
		{
			//Last minute check to confirm whether waiting is required
			var currentEntry = await GetAsync<T>(cacheKey).ConfigureAwait(false);
			if (currentEntry != null && currentEntry.GetStaleDate(settings) > DateTimeProvider.Now)
			{
				KeyLock.ReleaseLock(cacheKey, new RefreshResultEntry<T>(currentEntry, false));
				return CachedResult(currentEntry, 0, CacheValueFreshness.Fresh);
			}

			//Lock until we are notified to be unlocked
			return await WaitForRefreshAsync<T>(KeyLock, cacheKey, cancellationToken).ConfigureAwait(false);
		}

		return default;
	}

	/// <summary>
	/// Waits for the single in-flight refresh for <paramref name="cacheKey"/> while allowing only this caller to give up waiting.
	/// </summary>
	/// <remarks>
	/// Cancellation does not touch the shared refresh or its lock - other callers still receive the refresh result.
	/// </remarks>
	private static async ValueTask<CacheReadResult<T>?> WaitForRefreshAsync<T>(CacheEntryKeyLock keyLock, string cacheKey, CancellationToken cancellationToken)
	{
		var refreshTask = keyLock.WaitAsync(cacheKey);

		if (cancellationToken.CanBeCanceled)
		{
			var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
			using var registration = cancellationToken.Register(static state => ((TaskCompletionSource<bool>)state!).TrySetResult(true), tcs);
			var completed = await Task.WhenAny(refreshTask, tcs.Task).ConfigureAwait(false);
			if (!ReferenceEquals(completed, refreshTask))
			{
				//Observe the refresh separately so detaching this wait does not surface an unobserved exception.
				_ = refreshTask.ContinueWith(static _ => { }, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
				throw new OperationCanceledException(cancellationToken);
			}
		}

		var notification = await refreshTask.ConfigureAwait(false);
		if (notification is RefreshResultEntry<T> refreshResult)
		{
			return new CacheReadResult<T>(
				refreshResult.CacheEntry.Value,
				refreshResult.ServedFromFactory ? CacheValueSource.Factory : CacheValueSource.MemoryLayer,
				CacheValueFreshness.Fresh,
				refreshResult.ServedFromFactory ? null : 0
			);
		}

		var cacheEntry = (CacheEntry<T>)notification;
		return new CacheReadResult<T>(cacheEntry.Value, CacheValueSource.MemoryLayer, CacheValueFreshness.Fresh, 0);
	}

	/// <inheritdoc/>
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
