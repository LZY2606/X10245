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
	private readonly struct CacheEntryPoint<T>
	{
		public CacheEntryPoint(int layerIndex, CacheEntry<T> cacheEntry)
		{
			LayerIndex = layerIndex;
			CacheEntry = cacheEntry;
		}

		public int LayerIndex { get; }
		public CacheEntry<T> CacheEntry { get; }
	}

	private readonly struct RefreshResult<T>
	{
		public RefreshResult(CacheEntry<T>? cacheEntry, bool evictionDetected, bool fromFactory, int layerIndex = -1)
		{
			CacheEntry = cacheEntry;
			EvictionDetected = evictionDetected;
			FromFactory = fromFactory;
			LayerIndex = layerIndex;
		}

		public CacheEntry<T>? CacheEntry { get; }
		public bool EvictionDetected { get; }
		public bool FromFactory { get; }
		public int LayerIndex { get; }
	}

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

		long evictionGeneration;
		while (!KeyLock.BeginEviction(cacheKey, out evictionGeneration, out var activeOperation))
		{
			await activeOperation.ConfigureAwait(false);
		}

		try
		{
			for (int i = 0, l = CacheLayers.Length; i < l; i++)
			{
				var layer = CacheLayers[i];
				await layer.EvictAsync(cacheKey).ConfigureAwait(false);
			}

			await Extensions.OnCacheEvictionAsync(cacheKey).ConfigureAwait(false);
		}
		finally
		{
			KeyLock.EndEviction(cacheKey, evictionGeneration);
		}
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
		if (cacheEntryPoint == null)
		{
			return default;
		}

		var entryPoint = cacheEntryPoint.GetValueOrDefault();
		if (entryPoint.LayerIndex == 0)
		{
			return entryPoint.CacheEntry;
		}

		_ = BackPopulateCacheAsync(entryPoint.LayerIndex, cacheKey, entryPoint.CacheEntry);

		return entryPoint.CacheEntry;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static CacheValueSource GetLayerSource(int layerIndex)
	{
		return layerIndex == 0 ? CacheValueSource.MemoryLayer : CacheValueSource.BackupLayer;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static CacheReadReceipt CreateReceipt(CacheValueSource source, int layerIndex, DateTime? expiry)
	{
		return new CacheReadReceipt(source, layerIndex, expiry);
	}

	private async ValueTask<CacheEntryPoint<T>?> GetWithLayerIndexAsync<T>(string cacheKey)
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
							return new CacheEntryPoint<T>(layerIndex, cacheEntry);
						}
				}
				catch (CacheSerializationException ex)
				{
					Logger.LogWarning(ex, "Unable to retrieve CacheKey={CacheKey} from CacheLayer={CacheLayer} due to an exception. This layer will be skipped.", cacheKey, layer.GetType());
				}
			}
		}

		return null;
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

		var result = await GetOrSetCoreAsync(cacheKey, valueFactory, settings, CancellationToken.None).ConfigureAwait(false);
		return result.Value!;
	}

	/// <inheritdoc/>
	public async ValueTask<CacheReadResult<T>> GetOrSetWithReceiptAsync<T>(string cacheKey, Func<T, Task<T>> valueFactory, CacheSettings settings, CancellationToken cancellationToken = default)
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

		return await GetOrSetCoreAsync(cacheKey, valueFactory, settings, cancellationToken).ConfigureAwait(false);
	}

	private async ValueTask<CacheReadResult<T>> GetOrSetCoreAsync<T>(string cacheKey, Func<T, Task<T>> valueFactory, CacheSettings settings, CancellationToken cancellationToken)
	{
		while (true)
		{
			cancellationToken.ThrowIfCancellationRequested();

			var currentTime = DateTimeProvider.Now;
			var cacheEntryPoint = await GetWithLayerIndexAsync<T>(cacheKey).ConfigureAwait(false);

			if (cacheEntryPoint != null)
			{
				var entryPoint = cacheEntryPoint.GetValueOrDefault();
				var cacheEntry = entryPoint.CacheEntry;
				var layerSource = GetLayerSource(entryPoint.LayerIndex);

				if (cacheEntry.Expiry > currentTime)
				{
					var isStale = settings.StaleAfter.HasValue && cacheEntry.GetStaleDate(settings) < currentTime;
					if (isStale)
					{
						_ = RefreshValueAsync(cacheKey, valueFactory, settings, CacheEntryStatus.Stale, CancellationToken.None);
						return new CacheReadResult<T>(cacheEntry.Value, CreateReceipt(layerSource | CacheValueSource.Stale, entryPoint.LayerIndex, cacheEntry.Expiry));
					}

					if (entryPoint.LayerIndex > 0)
					{
						_ = BackPopulateCacheAsync(entryPoint.LayerIndex, cacheKey, cacheEntry);
					}

					return new CacheReadResult<T>(cacheEntry.Value, CreateReceipt(layerSource | CacheValueSource.Fresh, entryPoint.LayerIndex, cacheEntry.Expiry));
				}

				if (settings.StaleWhileRevalidate.HasValue &&
					cacheEntry.Expiry + settings.StaleWhileRevalidate.Value >= currentTime)
				{
					_ = RefreshValueAsync(cacheKey, valueFactory, settings, CacheEntryStatus.Expired, CancellationToken.None);
					return new CacheReadResult<T>(cacheEntry.Value, CreateReceipt(layerSource | CacheValueSource.Stale, entryPoint.LayerIndex, cacheEntry.Expiry));
				}
			}

			var cacheEntryStatus = cacheEntryPoint == null ? CacheEntryStatus.Miss : CacheEntryStatus.Expired;
			var refreshResult = await RefreshValueAsync(cacheKey, valueFactory, settings, cacheEntryStatus, cancellationToken).ConfigureAwait(false);

			if (refreshResult.EvictionDetected)
			{
				continue;
			}

			var refreshedEntry = refreshResult.CacheEntry;
			if (refreshedEntry == null)
			{
				throw new InvalidOperationException("The cache refresh completed without producing a value.");
			}

			if (refreshResult.FromFactory)
			{
				return new CacheReadResult<T>(refreshedEntry.Value, CreateReceipt(CacheValueSource.Factory | CacheValueSource.Fresh, -1, refreshedEntry.Expiry));
			}

			var source = GetLayerSource(refreshResult.LayerIndex) | CacheValueSource.Fresh;
			return new CacheReadResult<T>(refreshedEntry.Value, CreateReceipt(source, refreshResult.LayerIndex, refreshedEntry.Expiry));
		}
	}

	private async ValueTask BackPopulateCacheAsync<T>(int fromIndexExclusive, string cacheKey, CacheEntry<T> cacheEntry)
	{
		if (!KeyLock.AcquireLock(cacheKey, out var generation))
		{
			return;
		}

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
			KeyLock.ReleaseLock(cacheKey, generation, cacheEntry);
		}
	}

	private static async Task<ICacheEntry> WaitForRefreshAsync(Task<ICacheEntry> refreshTask, CancellationToken cancellationToken)
	{
		if (!cancellationToken.CanBeCanceled)
		{
			return await refreshTask.ConfigureAwait(false);
		}

		var cancellationCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		using (cancellationToken.Register(state => ((TaskCompletionSource<bool>)state).TrySetResult(true), cancellationCompletion))
		{
			var completedTask = await Task.WhenAny(refreshTask, cancellationCompletion.Task).ConfigureAwait(false);
			if (completedTask != refreshTask)
			{
				cancellationToken.ThrowIfCancellationRequested();
			}

			return await refreshTask.ConfigureAwait(false);
		}
	}

	private async ValueTask<RefreshResult<T>> RefreshValueAsync<T>(
		string cacheKey,
		Func<T, Task<T>> asyncValueFactory,
		CacheSettings settings,
		CacheEntryStatus entryStatus,
		CancellationToken cancellationToken)
	{
		if (KeyLock.AcquireLock(cacheKey, out var generation))
		{
			try
			{
				var previousEntryPoint = await GetWithLayerIndexAsync<T>(cacheKey).ConfigureAwait(false);
				var previousEntry = previousEntryPoint?.CacheEntry;
				if (previousEntry != default && entryStatus == CacheEntryStatus.Miss && previousEntry.Expiry >= DateTimeProvider.Now)
				{
					var previousPoint = previousEntryPoint.GetValueOrDefault();
					KeyLock.ReleaseLock(cacheKey, generation, previousEntry);
					return new RefreshResult<T>(previousEntry, false, false, previousPoint.LayerIndex);
				}

				await using var distributedLock = await Extensions.AwaitAccessAsync(cacheKey).ConfigureAwait(false);

				if (distributedLock.IsLockOwner)
				{
					if (KeyLock.IsEvictionInProgress(cacheKey, generation))
					{
						KeyLock.ReleaseLockForEviction(cacheKey, generation);
						return new RefreshResult<T>(null, true, false);
					}

					var oldValue = previousEntry != default ? previousEntry.Value : default;
					var refreshedValue = await asyncValueFactory(oldValue!).ConfigureAwait(false);

					if (KeyLock.IsEvictionInProgress(cacheKey, generation))
					{
						KeyLock.ReleaseLockForEviction(cacheKey, generation);
						return new RefreshResult<T>(null, true, false);
					}

					var cacheEntry = new CacheEntry<T>(refreshedValue, settings.TimeToLive);
					var cacheUpdateType = entryStatus == CacheEntryStatus.Miss
						? CacheUpdateType.AddEntry
						: CacheUpdateType.AddOrUpdateEntry;
					await InternalSetAsync(cacheKey, cacheEntry, cacheUpdateType).ConfigureAwait(false);

					KeyLock.ReleaseLock(cacheKey, generation, cacheEntry);
					return new RefreshResult<T>(cacheEntry, false, true);
				}

				var currentEntryPoint = await GetWithLayerIndexAsync<T>(cacheKey).ConfigureAwait(false);
				if (currentEntryPoint == null)
				{
					KeyLock.ReleaseLockForEviction(cacheKey, generation);
					return new RefreshResult<T>(null, true, false);
				}

				var currentPoint = currentEntryPoint.GetValueOrDefault();
				KeyLock.ReleaseLock(cacheKey, generation, currentPoint.CacheEntry);
				return new RefreshResult<T>(currentPoint.CacheEntry, false, false, currentPoint.LayerIndex);
			}
			catch (Exception e)
			{
				KeyLock.ReleaseLock(cacheKey, generation, e);
				throw;
			}
		}

		if (entryStatus != CacheEntryStatus.Stale)
		{
			var currentEntryPoint = await GetWithLayerIndexAsync<T>(cacheKey).ConfigureAwait(false);
			if (currentEntryPoint != null)
			{
				var currentEntry = currentEntryPoint.GetValueOrDefault().CacheEntry;
				if (currentEntry.Expiry > DateTimeProvider.Now && currentEntry.GetStaleDate(settings) > DateTimeProvider.Now)
				{
					var currentPoint = currentEntryPoint.GetValueOrDefault();
					return new RefreshResult<T>(currentEntry, false, false, currentPoint.LayerIndex);
				}
			}

			cancellationToken.ThrowIfCancellationRequested();
			await WaitForRefreshAsync(KeyLock.WaitAsync(cacheKey), cancellationToken).ConfigureAwait(false);

			var refreshedEntryPoint = await GetWithLayerIndexAsync<T>(cacheKey).ConfigureAwait(false);
			if (refreshedEntryPoint == null)
			{
				return new RefreshResult<T>(null, true, false);
			}

			var refreshedPoint = refreshedEntryPoint.GetValueOrDefault();
			return new RefreshResult<T>(refreshedPoint.CacheEntry, false, false, refreshedPoint.LayerIndex);
		}

		return new RefreshResult<T>(null, false, false);
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
