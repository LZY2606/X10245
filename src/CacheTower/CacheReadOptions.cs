using System;
using System.Threading;

namespace CacheTower;

/// <summary>
/// Selects how expired cache entries are treated during a read.
/// </summary>
public enum CacheReadPolicy
{
	/// <summary>
	/// The default policy. Expired entries are never returned - an expired entry forces a foreground refresh through the value factory.
	/// This preserves the historical behaviour of <see cref="ICacheStack.GetOrSetAsync{T}(string, Func{T, Task{T}}, CacheSettings)"/>.
	/// </summary>
	Default,
	/// <summary>
	/// Opt-in stale-while-revalidate policy.
	/// An expired entry may be returned while it remains within the stale-while-revalidate window and a single background refresh is triggered for the key.
	/// </summary>
	StaleWhileRevalidate,
}

/// <summary>
/// Options for an individual cache read through <see cref="ICacheStack.GetOrSetAsync{T}(string, Func{T, Task{T}}, CacheSettings, CacheReadOptions, CancellationToken)"/>.
/// </summary>
/// <remarks>
/// <para>
/// The default value of this struct uses <see cref="CacheReadPolicy.Default"/> - existing read behaviour is unchanged unless stale-while-revalidate is explicitly opted into.
/// </para>
/// <para>
/// When <see cref="Policy"/> is <see cref="CacheReadPolicy.StaleWhileRevalidate"/>, <see cref="StaleWhileRevalidateWindow"/> is required and defines
/// how long after its expiry an entry may still be served stale. A failed refresh does not extend this window - it is calculated from the
/// expired entry itself and a later read within the window simply triggers another single-flight refresh.
/// </para>
/// <para>
/// The window endpoints are handled deterministically: an entry is stale-while-revalidate eligible when
/// <c>expiry &lt;= now &lt; expiry + window</c>. The expiry instant is included; the window end instant is excluded.
/// </para>
/// </remarks>
public readonly record struct CacheReadOptions
{
	/// <summary>
	/// The read policy to apply. Defaults to <see cref="CacheReadPolicy.Default"/>.
	/// </summary>
	public CacheReadPolicy Policy { get; init; } = CacheReadPolicy.Default;

	/// <summary>
	/// When using <see cref="CacheReadPolicy.StaleWhileRevalidate"/>, the length of time after expiry during which the stale entry may still be returned.
	/// </summary>
	/// <remarks>
	/// Must be set to a positive <see cref="TimeSpan"/> when <see cref="Policy"/> is <see cref="CacheReadPolicy.StaleWhileRevalidate"/>.
	/// Ignored for <see cref="CacheReadPolicy.Default"/>.
	/// </remarks>
	public TimeSpan? StaleWhileRevalidateWindow { get; init; }

	/// <summary>
	/// Creates read options for stale-while-revalidate with the given <paramref name="staleWhileRevalidateWindow"/>.
	/// </summary>
	/// <param name="staleWhileRevalidateWindow">How long after expiry the stale value may still be served while a refresh is in flight.</param>
	/// <returns></returns>
	public static CacheReadOptions StaleWhileRevalidate(TimeSpan staleWhileRevalidateWindow) => new()
	{
		Policy = CacheReadPolicy.StaleWhileRevalidate,
		StaleWhileRevalidateWindow = staleWhileRevalidateWindow
	};

	internal void Validate()
	{
		switch (Policy)
		{
			case CacheReadPolicy.Default:
				return;
			case CacheReadPolicy.StaleWhileRevalidate:
				if (!StaleWhileRevalidateWindow.HasValue)
				{
					throw new ArgumentException(
						$"A {nameof(StaleWhileRevalidateWindow)} must be provided when the read policy is {nameof(CacheReadPolicy.StaleWhileRevalidate)}.",
						nameof(CacheReadOptions));
				}

				if (StaleWhileRevalidateWindow.Value < TimeSpan.Zero)
				{
					throw new ArgumentOutOfRangeException(
						nameof(CacheReadOptions),
						StaleWhileRevalidateWindow.Value,
						$"The {nameof(StaleWhileRevalidateWindow)} must not be negative.");
				}

				return;
			default:
				throw new ArgumentOutOfRangeException(
					nameof(CacheReadOptions),
					Policy,
					$"Unknown {nameof(CacheReadPolicy)} value.");
		}
	}
}
