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

		var evictionLease = await KeyLock.AcquireEvictionAsync(cacheKey).ConfigureAwait(false);

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

		KeyLock.ReleaseEviction(evictionLease);

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
		var result = await GetOrSetCoreAsync(cacheKey, valueFactory, new CacheReadOptions(settings), CacheEntryStatus.Stale, null).ConfigureAwait(false);
		return result.Value!;
	}

	/// <inheritdoc/>
	public ValueTask<CacheReadResult<T>> GetOrSetAsync<T>(string cacheKey, Func<T, Task<T>> valueFactory, CacheReadOptions options)
	{
		return GetOrSetCoreAsync(cacheKey, valueFactory, options, CacheEntryStatus.Stale, null);
	}

	private async ValueTask<CacheReadResult<T>> GetOrSetCoreAsync<T>(
		string cacheKey,
		Func<T, Task<T>> valueFactory,
		CacheReadOptions options,
		CacheEntryStatus backgroundStatus,
		CacheEntry<T>? backgroundEntry
	)
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

		var settings = options.Settings;
		var cancellationToken = options.CancellationToken;
		cancellationToken.ThrowIfCancellationRequested();

		var currentTime = DateTimeProvider.Now;
		var cacheEntryPoint = backgroundEntry is null
			? await GetWithLayerIndexAsync<T>(cacheKey).ConfigureAwait(false)
			: (0, backgroundEntry);
		var cacheEntryStatus = CacheEntryStatus.Stale;

		if (cacheEntryPoint != default)
		{
			var cacheEntry = cacheEntryPoint.CacheEntry;
			if (cacheEntry.Expiry > currentTime)
			{
				var freshness = settings.StaleAfter.HasValue && cacheEntry.GetStaleDate(settings) <= currentTime
					? CacheReadFreshness.Stale
					: CacheReadFreshness.Fresh;

				if (freshness == CacheReadFreshness.Stale)
				{
					_ = RefreshValueAsync(cacheKey, valueFactory, options, backgroundStatus, cacheEntry);
				}
				else if (cacheEntryPoint.LayerIndex > 0)
				{
					_ = BackPopulateCacheAsync(cacheEntryPoint.LayerIndex, cacheKey, cacheEntry);
				}

				return new(cacheEntry.Value, CacheReadReceipt.FromLayer(cacheEntryPoint.LayerIndex, freshness));
			}

			var staleWindowEndsAt = cacheEntry.Expiry + options.StaleWhileRevalidateWindow;
			if (options.Mode == CacheReadMode.StaleWhileRevalidate && staleWindowEndsAt >= currentTime)
			{
				_ = RefreshValueAsync(cacheKey, valueFactory, options, CacheEntryStatus.Expired, cacheEntry);
				return new(cacheEntry.Value, CacheReadReceipt.FromLayer(cacheEntryPoint.LayerIndex, CacheReadFreshness.Stale));
			}

			cacheEntryStatus = CacheEntryStatus.Expired;
		}
		else
		{
			cacheEntryStatus = CacheEntryStatus.Miss;
		}

		var refreshed = await RefreshValueAsync(cacheKey, valueFactory, options, cacheEntryStatus, null).ConfigureAwait(false);
		return new(refreshed!.Value, CacheReadReceipt.FromFactory());
	}

	private async ValueTask BackPopulateCacheAsync<T>(int fromIndexExclusive, string cacheKey, CacheEntry<T> cacheEntry)
	{
		var evictionLease = await KeyLock.AcquireEvictionAsync(cacheKey).ConfigureAwait(false);
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
			KeyLock.ReleaseEviction(evictionLease);
		}
	}

	private static async Task<TResult> WaitAsync<TResult>(Task<TResult> task, CancellationToken cancellationToken)
	{
		if (!cancellationToken.CanBeCanceled)
		{
			return await task.ConfigureAwait(false);
		}

		var completionSource = new TaskCompletionSource<TResult>(TaskCreationOptions.RunContinuationsAsynchronously);
		using var registration = cancellationToken.Register(state => ((TaskCompletionSource<TResult>)state!).TrySetCanceled(), completionSource);
		var completedTask = await Task.WhenAny(task, completionSource.Task).ConfigureAwait(false);
		return await completedTask.ConfigureAwait(false);
	}

	private async ValueTask<CacheEntry<T>?> RefreshValueAsync<T>(
		string cacheKey,
		Func<T, Task<T>> asyncValueFactory,
		CacheReadOptions options,
		CacheEntryStatus entryStatus,
		CacheEntry<T>? knownEntry
	)
	{
		var refreshLease = await KeyLock.TryBeginRefreshAsync(cacheKey).ConfigureAwait(false);
		if (refreshLease is not null)
		{
			var lease = refreshLease.GetValueOrDefault();
			try
			{
				var previousEntry = knownEntry ?? await GetAsync<T>(cacheKey).ConfigureAwait(false);
				if (previousEntry != default && entryStatus == CacheEntryStatus.Miss && previousEntry.Expiry >= DateTimeProvider.Now)
				{
					KeyLock.CompleteRefresh(lease, previousEntry);
					return previousEntry;
				}

				await using var distributedLock = await Extensions.AwaitAccessAsync(cacheKey).ConfigureAwait(false);

				CacheEntry<T>? cacheEntry;
				if (distributedLock.IsLockOwner)
				{
					var oldValue = previousEntry != default ? previousEntry.Value : default;
					var refreshedValue = await asyncValueFactory(oldValue!).ConfigureAwait(false);
					cacheEntry = new CacheEntry<T>(refreshedValue, options.Settings.TimeToLive);
					if (!KeyLock.TryCommitRefresh(lease, cacheEntry))
					{
						KeyLock.CancelRefresh(lease);
						return null;
					}

					var cacheUpdateType = entryStatus == CacheEntryStatus.Miss
						? CacheUpdateType.AddEntry
						: CacheUpdateType.AddOrUpdateEntry;
					try
					{
						await InternalSetAsync(cacheKey, cacheEntry, cacheUpdateType).ConfigureAwait(false);
					}
					finally
					{
						KeyLock.ReleaseRefreshGate(lease, cacheEntry);
					}
				}
				else
				{
					cacheEntry = await GetAsync<T>(cacheKey).ConfigureAwait(false);
					if (cacheEntry == default)
					{
						throw new InvalidOperationException("Cache entry not found after distributed refresh.");
					}
				}

				KeyLock.CompleteRefresh(lease, cacheEntry);
				return cacheEntry;
			}
			catch (Exception e)
			{
				KeyLock.FailRefresh(lease, e);
				throw;
			}
		}

		if (entryStatus == CacheEntryStatus.Stale)
		{
			return default;
		}

		var waitTask = KeyLock.RegisterWaiter(cacheKey);
		ICacheEntry awaitedEntry;
		try
		{
			awaitedEntry = await WaitAsync(waitTask, options.CancellationToken).ConfigureAwait(false);
		}
		catch
		{
			KeyLock.ReleaseWaiter(cacheKey);
			throw;
		}

		if (awaitedEntry is not CacheEntry<T> typedEntry)
		{
			KeyLock.ReleaseWaiter(cacheKey);
			throw new InvalidOperationException("Cache refresh completed with an incompatible entry type.");
		}

		return typedEntry;
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
