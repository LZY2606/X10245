using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CacheTower
{
	/// <summary>
	/// An <see cref="ICacheStack"/> is the backbone for Cache Tower. It is the primary user-facing type for interacting with the cache.
	/// </summary>
	public interface ICacheStack
	{
		/// <summary>
		/// Triggers the cleanup of any cache entries across the stack.
		/// </summary>
		/// <remarks>
		/// Some cache layers, like Redis, provide automatic cleanup of expired entries whereas other cache layers do not.
		/// <br/>
		/// This is used by <see cref="Extensions.AutoCleanupExtension"/> where cache layers are cleaned up on a timer.
		/// </remarks>
		/// <returns></returns>
		ValueTask CleanupAsync();
		/// <summary>
		/// Removes an entry with the corresponding <paramref name="cacheKey"/> from all cache layers.
		/// </summary>
		/// <param name="cacheKey">The cache entry's key.</param>
		/// <returns></returns>
		ValueTask EvictAsync(string cacheKey);
		/// <summary>
		/// Sets the entry for <paramref name="cacheKey"/> to <paramref name="value"/> with a corresponding <paramref name="timeToLive"/> across all cache layers.
		/// </summary>
		/// <typeparam name="T">The type of <paramref name="value"/>.</typeparam>
		/// <param name="cacheKey">The cache entry's key.</param>
		/// <param name="value">The value to store in the cache.</param>
		/// <param name="timeToLive">How long till the entry is expired.</param>
		/// <returns></returns>
		ValueTask<CacheEntry<T>> SetAsync<T>(string cacheKey, T value, TimeSpan timeToLive);
		/// <summary>
		/// Sets the <paramref name="cacheEntry"/> to a specific <paramref name="cacheKey"/> across all cache layers.
		/// </summary>
		/// <typeparam name="T">The type of value in <paramref name="cacheEntry"/>.</typeparam>
		/// <param name="cacheKey">The cache entry's key.</param>
		/// <param name="cacheEntry">The cache entry to set.</param>
		/// <returns></returns>
		ValueTask SetAsync<T>(string cacheKey, CacheEntry<T> cacheEntry);
		/// <summary>
		/// Retrieves the <see cref="CacheEntry{T}"/> for a given <paramref name="cacheKey"/>.
		/// </summary>
		/// <remarks>
		/// The entry returned corresponds to the first cache layer that contains it.
		/// <br/>
		/// If no cache layer contains it, Null is returned.
		/// <br/>
		/// <br/>
		/// Use <see cref="GetAsync{T}(string, bool)"/> to optionally perform back-population to upper cache layers if the entry is found in a lower cache layer.
		/// </remarks>
		/// <typeparam name="T">The type of value in the cache entry.</typeparam>
		/// <param name="cacheKey">The cache entry's key.</param>
		/// <returns></returns>
		ValueTask<CacheEntry<T>?> GetAsync<T>(string cacheKey);
		/// <summary>
		/// Retrieves the <see cref="CacheEntry{T}"/> for a given <paramref name="cacheKey"/> and 
		/// optionally back-populates the entry if it is found in a lower cache layer.
		/// </summary>
		/// <remarks>
		/// The entry returned corresponds to the first cache layer that contains it.
		/// <br/>
		/// If no cache layer contains it, Null is returned.
		/// <br/>
		/// <br/>
		/// Specifying a <paramref name="backPopulate"/> value of <see langword="false"/> is equivalent to calling <see cref="GetAsync{T}(string)"/>.
		/// </remarks>
		/// <typeparam name="T">The type of value in the cache entry.</typeparam>
		/// <param name="cacheKey">The cache entry's key.</param>
		/// <param name="backPopulate"><see langword="true"/> to back-populate the entry to upper cache layers if it is found in a lower cache layer; otherwise, <see langword="false"/>.</param>
		/// <returns></returns>
		ValueTask<CacheEntry<T>?> GetAsync<T>(string cacheKey, bool backPopulate);
		/// <summary>
		/// Attempts to retrieve the value for the given <paramref name="cacheKey"/>.
		/// When unavailable, will fallback to use <paramref name="valueFactory"/> to generate the value, storing it in the cache.
		/// </summary>
		/// <typeparam name="T">The type of value in the cache entry.</typeparam>
		/// <param name="cacheKey">The cache entry's key.</param>
		/// <param name="valueFactory">The value factory called when no cache entry is available.</param>
		/// <param name="settings">Cache control settings.</param>
		/// <returns>The item from the cache that corresponds to the given <paramref name="cacheKey"/>.</returns>
		ValueTask<T> GetOrSetAsync<T>(string cacheKey, Func<T, Task<T>> valueFactory, CacheSettings settings);
		/// <summary>
		/// Attempts to retrieve the value for the given <paramref name="cacheKey"/> with cancellation support.
		/// When unavailable, will fallback to use <paramref name="valueFactory"/> to generate the value, storing it in the cache.
		/// </summary>
		/// <remarks>
		/// Cancelling <paramref name="cancellationToken"/> only unblocks the calling read.
		/// A refresh shared with other reads is never cancelled by one caller - the single in-flight refresh continues and other callers still receive its result.
		/// </remarks>
		/// <typeparam name="T">The type of value in the cache entry.</typeparam>
		/// <param name="cacheKey">The cache entry's key.</param>
		/// <param name="valueFactory">The value factory called when no cache entry is available.</param>
		/// <param name="settings">Cache control settings.</param>
		/// <param name="cancellationToken">The cancellation token for the calling read. It is never passed to the shared value factory.</param>
		/// <returns>The item from the cache that corresponds to the given <paramref name="cacheKey"/>.</returns>
		ValueTask<T> GetOrSetAsync<T>(string cacheKey, Func<T, Task<T>> valueFactory, CacheSettings settings, CancellationToken cancellationToken);
		/// <summary>
		/// Attempts to retrieve the value for the given <paramref name="cacheKey"/> using the supplied <paramref name="readPolicy"/>.
		/// When unavailable, will fallback to use <paramref name="valueFactory"/> to generate the value, storing it in the cache.
		/// The returned <see cref="CacheReadResult{T}"/> provides the value plus a receipt describing where it came from.
		/// </summary>
		/// <remarks>
		/// With <see cref="CacheReadPolicy.StaleWhileRevalidate"/>, an entry that expired within
		/// <see cref="CacheSettings.StaleWhileRevalidate"/> is returned once as stale while a single shared refresh runs in the background.
		/// Concurrent reads for the same key join that refresh and never invoke the factory more than once.
		/// <para>
		/// The receipt never changes the returned value - <see cref="CacheReadResult{T}.Value"/> is the same value a plain
		/// <see cref="GetAsync{T}(string)"/> or <c>GetOrSetAsync</c> call would produce.
		/// </para>
		/// </remarks>
		/// <typeparam name="T">The type of value in the cache entry.</typeparam>
		/// <param name="cacheKey">The cache entry's key.</param>
		/// <param name="valueFactory">The value factory called when no cache entry is available.</param>
		/// <param name="settings">Cache control settings.</param>
		/// <param name="readPolicy">Whether expired entries may be served stale while a refresh runs.</param>
		/// <param name="cancellationToken">The cancellation token for the calling read. It is never passed to the shared value factory.</param>
		/// <returns>The value for the read and a receipt describing its source and freshness.</returns>
		ValueTask<CacheReadResult<T>> GetOrSetWithReceiptAsync<T>(string cacheKey, Func<T, Task<T>> valueFactory, CacheSettings settings, CacheReadPolicy readPolicy = CacheReadPolicy.Default, CancellationToken cancellationToken = default);
	}

	/// <remarks>
	/// An <see cref="IFlushableCacheStack"/> exposes an extra method to completely clear all the data from a cache stack.
	/// This is intentionally exposed as a separate interface in an attempt to prevent developers from inadvertently clearing all their cache data in production.
	/// </remarks>
	/// <inheritdoc/>
	public interface IFlushableCacheStack : ICacheStack
	{
		/// <summary>
		/// Flushes all cache layers, removing every item from the cache.
		/// </summary>
		/// <remarks>
		/// Warning: Do not call this unless you understand the gravity of clearing all cache layers entirely.
		/// <br/>
		/// Additionally, when using the `RedisRemoteEvictionExtension`, the flushing of caches is co-ordinated across all instances.
		/// </remarks>
		/// <returns></returns>
		ValueTask FlushAsync();
	}

	/// <remarks>
	/// An <see cref="IExtendableCacheStack"/> exposes an extra method to access the cache layers that make up a cache stack.
	/// <para>
	/// This is intentionally exposed as a separate interface in an attempt to prevent developers from directly interfacing with cache layers.
	/// This interface then is designed for extensions like the <c>RedisRemoteEvictionExtension</c> where access to the cache layers is directly required.
	/// </para>
	/// </remarks>
	/// <inheritdoc/>
	public interface IExtendableCacheStack : ICacheStack
	{
		/// <summary>
		/// Provides a list of the <see cref="ICacheLayer"/> instances that make up the cache stack.
		/// </summary>
		/// <returns></returns>
		IReadOnlyList<ICacheLayer> GetCacheLayers();
	}

	/// <remarks>
	/// An <see cref="ICacheStack{TContext}"/> provides an additional <c>GetOrSetAsync</c> method that passes in a context object.
	/// This context allows easier access to inject dependencies like database contexts or any other type needed to help generate the item to cache.
	/// </remarks>
	/// <typeparam name="TContext">The type of context that is passed during the cache entry generation process.</typeparam>
	/// <inheritdoc/>
	public interface ICacheStack<out TContext> : ICacheStack
	{
		/// <remarks>
		/// Additionally provides access to a context object during refreshing, allowing easier access to inject dependencies.
		/// </remarks>
		/// <inheritdoc cref="ICacheStack.GetOrSetAsync{T}(string, Func{T, Task{T}}, CacheSettings)"/>
		ValueTask<T> GetOrSetAsync<T>(string cacheKey, Func<T, TContext, Task<T>> valueFactory, CacheSettings settings);
		/// <remarks>Additionally provides access to a context object during refreshing.</remarks>
		/// <inheritdoc cref="ICacheStack.GetOrSetAsync{T}(string, Func{T, Task{T}}, CacheSettings, CancellationToken)"/>
		ValueTask<T> GetOrSetAsync<T>(string cacheKey, Func<T, TContext, Task<T>> valueFactory, CacheSettings settings, CancellationToken cancellationToken);
		/// <remarks>Additionally provides access to a context object during refreshing.</remarks>
		/// <inheritdoc cref="ICacheStack.GetOrSetWithReceiptAsync{T}(string, Func{T, Task{T}}, CacheSettings, CacheReadPolicy, CancellationToken)"/>
		ValueTask<CacheReadResult<T>> GetOrSetWithReceiptAsync<T>(string cacheKey, Func<T, TContext, Task<T>> valueFactory, CacheSettings settings, CacheReadPolicy readPolicy = CacheReadPolicy.Default, CancellationToken cancellationToken = default);
	}
}
