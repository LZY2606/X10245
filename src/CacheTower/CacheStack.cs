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
		ThrowIfDisposed();
		return (await GetOrSetCoreAsync(cacheKey, valueFactory, settings, null, CancellationToken.None).ConfigureAwait(false)).Value!;
	}

	/// <inheritdoc/>
	public async ValueTask<CacheReadResult<T>> GetOrSetAsync<T>(string cacheKey, Func<T, Task<T>> valueFactory, CacheSettings settings, CacheReadOptions readOptions)
	{
		ThrowIfDisposed();
		return await GetOrSetCoreAsync(cacheKey, valueFactory, settings, readOptions, readOptions.CancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// Shared read core used by both the default read strategy and the opt-in stale-while-revalidate read.
	/// </summary>
	/// <remarks>
	/// When <paramref name="readOptions"/> is <see langword="null"/>, the value's expiry boundary and stale handling
	/// follow the pre-existing default strategy exactly. When provided, expired entries within the
	/// stale-while-revalidate window are served stale with a single shared background refresh.
	/// </remarks>
	private async ValueTask<CacheReadResult<T>> GetOrSetCoreAsync<T>(
		string cacheKey,
		Func<T, Task<T>> valueFactory,
		CacheSettings settings,
		CacheReadOptions? readOptions,
		CancellationToken cancellationToken)
	{
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

				return CacheReadResult<T>.FromCache(cacheEntry.Value, cacheEntryPoint.LayerIndex, false);
			}

			if (readOptions.HasValue && cacheEntry.Expiry + readOptions.Value.StaleWhileRevalidate >= currentTime)
			{
				//Serve the expired-but-within-window value and ensure only a single shared refresh is in flight
				ObserveFault(RefreshValueAsync(cacheKey, valueFactory, settings, cacheEntryStatus));
				return CacheReadResult<T>.FromCache(cacheEntry.Value, cacheEntryPoint.LayerIndex, true);
			}

			//We never return expired data under the default strategy (nor past the stale-while-revalidate window)
			cacheEntryStatus = CacheEntryStatus.Expired;
		}
		else
		{
			//Refresh the value in the current thread because we have no existing data
			cacheEntryStatus = CacheEntryStatus.Miss;
		}

		var refreshedEntry = await RefreshForegroundAsync(cacheKey, valueFactory, settings, cacheEntryStatus, cancellationToken).ConfigureAwait(false);
		return CacheReadResult<T>.FromFactory(refreshedEntry!.Value);
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

	/// <summary>
	/// Performs a fire-and-forget background refresh of a stale entry. Failures don't reach a caller.
	/// </summary>
	private async ValueTask RefreshValueAsync<T>(string cacheKey, Func<T, Task<T>> asyncValueFactory, CacheSettings settings, CacheEntryStatus entryStatus)
	{
		if (KeyLock.AcquireLock(cacheKey))
		{
			await RunRefreshAsync(cacheKey, asyncValueFactory, settings, entryStatus).ConfigureAwait(false);
		}
	}

	/// <summary>
	/// Performs the foreground refresh shared by cache misses, expired entries and stale-while-revalidate window expiries.
	/// </summary>
	/// <remarks>
	/// Exactly one caller per key owns the refresh; everyone else joins the same wait.
	/// <paramref name="cancellationToken"/> only abandons a waiter - it never cancels the owner's shared refresh.
	/// </remarks>
	private ValueTask<CacheEntry<T>?> RefreshForegroundAsync<T>(string cacheKey, Func<T, Task<T>> asyncValueFactory, CacheSettings settings, CacheEntryStatus entryStatus, CancellationToken cancellationToken)
	{
		if (KeyLock.AcquireLock(cacheKey))
		{
			if (cancellationToken.CanBeCanceled)
			{
				//Become a waiter on the refresh this call just started so a cancelling caller abandons its wait without cancelling the shared refresh.
				var waitTask = KeyLock.WaitAsync(cacheKey, cancellationToken);
				ObserveFault(RunRefreshAsync(cacheKey, asyncValueFactory, settings, entryStatus));
				return WaitForResultAsync<T>(waitTask);
			}

			return RunRefreshAsync(cacheKey, asyncValueFactory, settings, entryStatus);
		}

		return WaitForRefreshAsync<T>(cacheKey, settings, cancellationToken);
	}

	private static async ValueTask<CacheEntry<T>?> WaitForResultAsync<T>(Task<ICacheEntry> waitTask)
	{
		return await waitTask.ConfigureAwait(false) as CacheEntry<T>;
	}

	private async ValueTask<CacheEntry<T>?> WaitForRefreshAsync<T>(string cacheKey, CacheSettings settings, CancellationToken cancellationToken)
	{
		//Last minute check to confirm whether waiting is required
		var currentEntry = await GetAsync<T>(cacheKey).ConfigureAwait(false);
		if (currentEntry != null && currentEntry.GetStaleDate(settings) > DateTimeProvider.Now)
		{
			KeyLock.ReleaseLock(cacheKey, currentEntry);
			return currentEntry;
		}

		//Lock until we are notified to be unlocked
		return await KeyLock.WaitAsync(cacheKey, cancellationToken).ConfigureAwait(false) as CacheEntry<T>;
	}

	private async ValueTask<CacheEntry<T>?> RunRefreshAsync<T>(string cacheKey, Func<T, Task<T>> asyncValueFactory, CacheSettings settings, CacheEntryStatus entryStatus)
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
				await InternalSetAsync(cacheKey, cacheEntry, cacheUpdateType).ConfigureAwait(false);

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

	/// <summary>
	/// Observes a detached refresh so a factory failure can't surface as an unobserved exception.
	/// The refresh itself has already released the key lock (with the exception) for any joiners.
	/// </summary>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static void ObserveFault(ValueTask valueTask)
	{
		_ = valueTask.AsTask().ContinueWith(static task =>
		{
			if (task.IsFaulted)
			{
				_ = task.Exception;
			}
		}, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static void ObserveFault<T>(ValueTask<T> valueTask)
	{
		_ = valueTask.AsTask().ContinueWith(static task =>
		{
			if (task.IsFaulted)
			{
				_ = task.Exception;
			}
		}, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
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
