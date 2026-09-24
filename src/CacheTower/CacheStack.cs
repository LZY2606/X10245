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

		//Record the eviction before announcing it so any in-flight refresh
		//observes the bumped generation and cannot resurrect the evicted value.
		KeyLock.NotifyEvicted(cacheKey);

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
		var result = await GetOrSetCoreAsync(cacheKey, valueFactory, settings, CacheReadOptions.Default, CancellationToken.None).ConfigureAwait(false);
		return result.Value!;
	}

	/// <inheritdoc/>
	public ValueTask<CacheGetResult<T>> GetOrSetAsync<T>(string cacheKey, Func<T, Task<T>> valueFactory, CacheSettings settings, CacheReadOptions readOptions, CancellationToken cancellationToken = default)
	{
		return GetOrSetCoreAsync(cacheKey, valueFactory, settings, readOptions, cancellationToken);
	}

	private async ValueTask<CacheGetResult<T>> GetOrSetCoreAsync<T>(
		string cacheKey,
		Func<T, Task<T>> valueFactory,
		CacheSettings settings,
		CacheReadOptions readOptions,
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
		if (cacheEntryPoint != default)
		{
			var cacheEntry = cacheEntryPoint.CacheEntry;
			if (cacheEntry.Expiry > currentTime)
			{
				if (settings.StaleAfter.HasValue && cacheEntry.GetStaleDate(settings) < currentTime)
				{
					//If the cache entry is stale (but not expired), refresh the value in the background
					_ = RefreshValueAsync(cacheKey, valueFactory, settings, CacheEntryStatus.Stale);
				}
				else if (cacheEntryPoint.LayerIndex > 0)
				{
					//If a lower-level cache is missing the latest data, attempt to set it in the background
					_ = BackPopulateCacheAsync(cacheEntryPoint.LayerIndex, cacheKey, cacheEntry);
				}

				var freshReceipt = new CacheReadReceipt(
					cacheEntryPoint.LayerIndex == 0 ? CacheValueSource.MemoryLayer : CacheValueSource.BackupLayer,
					CacheValueFreshness.Fresh,
					cacheEntryPoint.LayerIndex
				);
				return new CacheGetResult<T>(cacheEntry.Value!, freshReceipt);
			}

			if (readOptions.Policy == CacheReadPolicy.StaleWhileRevalidate
				&& cacheEntry.Expiry + readOptions.StaleWhileRevalidateWindow >= currentTime)
			{
				//Serve the stale/expired value within the allowed window and refresh once in the background.
				//The background refresh is never linked to this caller's cancellation token -
				//cancelling a caller must not cancel the shared refresh.
				_ = RefreshValueAsync(cacheKey, valueFactory, settings, CacheEntryStatus.Stale);

				var staleReceipt = new CacheReadReceipt(
					cacheEntryPoint.LayerIndex == 0 ? CacheValueSource.MemoryLayer : CacheValueSource.BackupLayer,
					CacheValueFreshness.Stale,
					cacheEntryPoint.LayerIndex
				);
				return new CacheGetResult<T>(cacheEntry.Value!, staleReceipt);
			}

			//Refresh the value in the current thread because we only have expired data (we never return expired data under the default policy)
			var refreshedExpired = await RefreshValueAsync(cacheKey, valueFactory, settings, CacheEntryStatus.Expired, cancellationToken).ConfigureAwait(false);
			return new CacheGetResult<T>(refreshedExpired!.Value!, FactoryFreshReceipt);
		}

		//Refresh the value in the current thread because we have no existing data
		var refreshedMiss = await RefreshValueAsync(cacheKey, valueFactory, settings, CacheEntryStatus.Miss, cancellationToken).ConfigureAwait(false);
		return new CacheGetResult<T>(refreshedMiss!.Value!, FactoryFreshReceipt);
	}

	private static readonly CacheReadReceipt FactoryFreshReceipt = new(CacheValueSource.Factory, CacheValueFreshness.Fresh, -1);

	private async ValueTask BackPopulateCacheAsync<T>(int fromIndexExclusive, string cacheKey, CacheEntry<T> cacheEntry)
	{
		if (KeyLock.AcquireLock(cacheKey, out var evictionGeneration))
		{
			try
			{
				for (; --fromIndexExclusive >= 0;)
				{
					//An eviction during back-population means the value must not be resurrected in an upper layer.
					if (KeyLock.GetEvictionGeneration(cacheKey) != evictionGeneration)
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
				KeyLock.ReleaseLock(cacheKey, evictionGeneration, (ICacheEntry?)cacheEntry);
			}
		}
	}

	private ValueTask<CacheEntry<T>?> RefreshValueAsync<T>(string cacheKey, Func<T, Task<T>> asyncValueFactory, CacheSettings settings, CacheEntryStatus entryStatus)
	{
		return RefreshValueAsync(cacheKey, asyncValueFactory, settings, entryStatus, CancellationToken.None);
	}

	private async ValueTask<CacheEntry<T>?> RefreshValueAsync<T>(string cacheKey, Func<T, Task<T>> asyncValueFactory, CacheSettings settings, CacheEntryStatus entryStatus, CancellationToken cancellationToken)
	{
		if (KeyLock.AcquireLock(cacheKey, out var evictionGeneration))
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
					KeyLock.ReleaseLock(cacheKey, evictionGeneration, previousEntry);
					return previousEntry;
				}

				await using var distributedLock = await Extensions.AwaitAccessAsync(cacheKey).ConfigureAwait(false);

				CacheEntry<T>? cacheEntry;
				if (distributedLock.IsLockOwner)
				{
					var oldValue = previousEntry != default ? previousEntry.Value : default;
					//The shared refresh never observes the caller's cancellation token -
					//cancelling a waiting caller must not cancel the refresh for everyone else.
					var refreshedValue = await asyncValueFactory(oldValue!).ConfigureAwait(false);
					cacheEntry = new CacheEntry<T>(refreshedValue, settings.TimeToLive);

					//Delete wins over refresh: if the key was evicted while the factory ran, do not resurrect the value.
					if (KeyLock.GetEvictionGeneration(cacheKey) != evictionGeneration)
					{
						KeyLock.ReleaseLock(cacheKey, evictionGeneration, cacheEntry);
						return cacheEntry;
					}

					var cacheUpdateType = entryStatus switch
					{
						CacheEntryStatus.Miss => CacheUpdateType.AddEntry,
						_ => CacheUpdateType.AddOrUpdateEntry
					};
					await InternalSetAsync(cacheKey, cacheEntry, cacheUpdateType).ConfigureAwait(false);

					KeyLock.ReleaseLock(cacheKey, evictionGeneration, cacheEntry);
				}
				else
				{
					cacheEntry = await GetAsync<T>(cacheKey).ConfigureAwait(false);
					KeyLock.ReleaseLock(cacheKey, evictionGeneration, cacheEntry);
				}

				return cacheEntry;
			}
			catch (Exception e)
			{
				//On failure no cache entry is touched - a failed refresh never extends the stale window
				//and no failure markers or temporary state are left behind in the cache.
				KeyLock.ReleaseLock(cacheKey, evictionGeneration, e);
				throw;
			}
		}
		else if (entryStatus != CacheEntryStatus.Stale)
		{
			//Last minute check to confirm whether waiting is required
			var currentEntry = await GetAsync<T>(cacheKey).ConfigureAwait(false);
			if (currentEntry != null && currentEntry.Expiry > DateTimeProvider.Now
				&& (!settings.StaleAfter.HasValue || currentEntry.GetStaleDate(settings) > DateTimeProvider.Now))
			{
				//We never acquired the lock here - return the fresh entry without touching the lock state.
				return currentEntry;
			}

			//Lock until we are notified to be unlocked.
			//Only the caller's wait is cancellable - the shared refresh itself keeps running.
			var waitTask = KeyLock.WaitAsync(cacheKey);
			ICacheEntry? result;
			if (cancellationToken.CanBeCanceled)
			{
				var tcs = new TaskCompletionSource<ICacheEntry?>(TaskCreationOptions.RunContinuationsAsynchronously);
				using (cancellationToken.Register(static state => ((TaskCompletionSource<ICacheEntry?>)state!).TrySetCanceled(), tcs))
				{
					var completed = await Task.WhenAny(waitTask, tcs.Task).ConfigureAwait(false);
					if (completed == tcs.Task)
					{
						//Observe the refresh outcome separately so the shared refresh's result/exception isn't left unobserved.
						_ = ObserveWaitAsync(waitTask);
						await tcs.Task.ConfigureAwait(false);
					}
					result = await waitTask.ConfigureAwait(false);
				}
			}
			else
			{
				result = await waitTask.ConfigureAwait(false);
			}

			return result as CacheEntry<T>;
		}

		return default;
	}

	private static async Task ObserveWaitAsync(Task<ICacheEntry> waitTask)
	{
		try
		{
			await waitTask.ConfigureAwait(false);
		}
		catch
		{
			//Deliberately swallowed - the original caller already gave up waiting.
		}
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
