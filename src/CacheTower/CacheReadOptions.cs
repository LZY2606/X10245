using System;
using System.Threading;

namespace CacheTower;

/// <summary>
/// Options for reading a value from a cache stack.
/// </summary>
public readonly record struct CacheReadOptions
{
	/// <summary>
	/// The read mode used by the cache stack.
	/// </summary>
	public CacheReadMode Mode { get; }

	/// <summary>
	/// The maximum amount of time after expiry that a stale value may be returned.
	/// </summary>
	public TimeSpan? StaleWhileRevalidateWindow { get; }

	/// <summary>
	/// A token used to cancel the caller's wait. It does not cancel a refresh shared with other callers.
	/// </summary>
	public CancellationToken CancellationToken { get; }

	/// <summary>
	/// Creates read options for the specified <paramref name="mode"/>.
	/// </summary>
	/// <param name="mode">The read mode to use.</param>
	/// <param name="staleWhileRevalidateWindow">The bounded window after expiry in which stale data may be returned.</param>
	/// <param name="cancellationToken">A token for cancelling the caller's wait.</param>
	/// <exception cref="ArgumentOutOfRangeException">
	/// Thrown when <paramref name="mode"/> is not defined, the window is not positive, or the options are not valid for the mode.
	/// </exception>
	public CacheReadOptions(
		CacheReadMode mode = CacheReadMode.Default,
		TimeSpan? staleWhileRevalidateWindow = null,
		CancellationToken cancellationToken = default
	)
	{
		if (!Enum.IsDefined(typeof(CacheReadMode), mode))
		{
			throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unsupported cache read mode");
		}

		if (mode == CacheReadMode.StaleWhileRevalidate)
		{
			if (!staleWhileRevalidateWindow.HasValue)
			{
				throw new ArgumentOutOfRangeException(
					nameof(staleWhileRevalidateWindow),
					"A stale-while-revalidate window is required"
				);
			}

			if (staleWhileRevalidateWindow.Value <= TimeSpan.Zero)
			{
				throw new ArgumentOutOfRangeException(
					nameof(staleWhileRevalidateWindow),
					staleWhileRevalidateWindow.Value,
					"The stale-while-revalidate window must be greater than zero"
				);
			}
		}
		else if (staleWhileRevalidateWindow.HasValue)
		{
			throw new ArgumentOutOfRangeException(
				nameof(staleWhileRevalidateWindow),
				staleWhileRevalidateWindow.Value,
				"A stale-while-revalidate window can only be used with stale-while-revalidate mode"
			);
		}

		Mode = mode;
		StaleWhileRevalidateWindow = staleWhileRevalidateWindow;
		CancellationToken = cancellationToken;
	}

	/// <summary>
	/// Gets the default read options.
	/// </summary>
	public static CacheReadOptions Default { get; } = new();

	/// <summary>
	/// Creates stale-while-revalidate read options.
	/// </summary>
	/// <param name="staleWhileRevalidateWindow">The bounded window after expiry in which stale data may be returned.</param>
	/// <param name="cancellationToken">A token for cancelling the caller's wait.</param>
	/// <returns>New stale-while-revalidate options.</returns>
	public static CacheReadOptions StaleWhileRevalidate(
		TimeSpan staleWhileRevalidateWindow,
		CancellationToken cancellationToken = default
	) => new(CacheReadMode.StaleWhileRevalidate, staleWhileRevalidateWindow, cancellationToken);
}
