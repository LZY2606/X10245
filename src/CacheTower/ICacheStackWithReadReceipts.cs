using System;
using System.Threading;
using System.Threading.Tasks;

namespace CacheTower;

/// <summary>
/// Provides cache reads that include a value-independent source receipt.
/// </summary>
public interface ICacheStackWithReadReceipts : ICacheStack
{
	/// <summary>
	/// Attempts to retrieve a value and returns a receipt describing whether it came from a cache layer or the value factory.
	/// </summary>
	/// <typeparam name="T">The type of value in the cache entry.</typeparam>
	/// <param name="cacheKey">The cache entry's key.</param>
	/// <param name="valueFactory">The value factory called when a foreground refresh is required.</param>
	/// <param name="settings">Cache control settings.</param>
	/// <param name="options">The read strategy options.</param>
	/// <param name="cancellationToken">Cancels waiting for the caller's result without cancelling a shared in-progress refresh.</param>
	/// <returns>The value and a receipt describing its source.</returns>
	ValueTask<CacheReadResult<T>> GetOrSetAsync<T>(
		string cacheKey,
		Func<T, Task<T>> valueFactory,
		CacheSettings settings,
		CacheReadOptions options,
		CancellationToken cancellationToken = default
	);
}

/// <summary>
/// Provides contextual cache reads that include a source receipt.
/// </summary>
/// <typeparam name="TContext">The type of context passed during a value refresh.</typeparam>
public interface ICacheStackWithReadReceipts<TContext> : ICacheStack<TContext>, ICacheStackWithReadReceipts
{
	/// <inheritdoc cref="ICacheStackWithReadReceipts.GetOrSetAsync{T}(string, Func{T, Task{T}}, CacheSettings, CacheReadOptions, CancellationToken)"/>
	/// <param name="cacheKey">The cache entry's key.</param>
	/// <param name="valueFactory">The value factory called when a foreground refresh is required.</param>
	/// <param name="settings">Cache control settings.</param>
	/// <param name="options">The read strategy options.</param>
	/// <param name="cancellationToken">Cancels waiting for the caller's result without cancelling a shared in-progress refresh.</param>
	ValueTask<CacheReadResult<T>> GetOrSetAsync<T>(
		string cacheKey,
		Func<T, TContext, Task<T>> valueFactory,
		CacheSettings settings,
		CacheReadOptions options,
		CancellationToken cancellationToken = default
	);
}
