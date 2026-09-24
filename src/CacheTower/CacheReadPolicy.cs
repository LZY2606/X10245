using System;

namespace CacheTower
{
	/// <summary>
	/// Controls how <see cref="ICacheStack"/> reads entries when requesting a value through an opt-in read options overload.
	/// </summary>
	public enum CacheReadPolicy
	{
		/// <summary>
		/// The default cache policy.
		/// <para>
		/// Fresh and stale-but-not-expired entries are returned immediately (a background refresh is still triggered for stale entries when configured).
		/// Expired entries are never returned - a foreground refresh through the value factory is forced.
		/// </para>
		/// </summary>
		Default = 0,
		/// <summary>
		/// Stale-while-revalidate.
		/// <para>
		/// Behaves like <see cref="Default"/> except that an expired entry may be returned while a single-flight background refresh runs,
		/// but only when the entry expired no longer ago than the configured stale-while-revalidate window.
		/// Outside that window a foreground refresh through the value factory is forced as usual.
		/// </para>
		/// </summary>
		StaleWhileRevalidate = 1
	}

	/// <summary>
	/// Identifies which cache layer served a value returned by an opt-in read.
	/// </summary>
	public enum CacheValueSource
	{
		/// <summary>
		/// The value was produced by the value factory - no cache layer served it.
		/// </summary>
		Factory = 0,
		/// <summary>
		/// The value was served by the first (highest priority) cache layer.
		/// </summary>
		MemoryLayer = 1,
		/// <summary>
		/// The value was served by a cache layer other than the first - for example a file or distributed fallback layer.
		/// </summary>
		BackupLayer = 2
	}

	/// <summary>
	/// Describes the freshness of a value returned by an opt-in read.
	/// </summary>
	public enum CacheValueFreshness
	{
		/// <summary>
		/// The value had not expired when it was served.
		/// </summary>
		Fresh = 0,
		/// <summary>
		/// The value was stale or expired (but still within the stale-while-revalidate window) when it was served.
		/// </summary>
		Stale = 1
	}

	/// <summary>
	/// Options for the opt-in read overloads on <see cref="ICacheStack"/>.
	/// </summary>
	public readonly record struct CacheReadOptions
	{
		/// <summary>
		/// The default read options - equivalent to <c>new CacheReadOptions()</c>.
		/// </summary>
		public static CacheReadOptions Default => new();

		/// <summary>
		/// The read policy to apply. Defaults to <see cref="CacheReadPolicy.Default"/>, keeping the standard cache behaviour.
		/// </summary>
		public CacheReadPolicy Policy { get; init; } = CacheReadPolicy.Default;

		/// <summary>
		/// When <see cref="Policy"/> is <see cref="CacheReadPolicy.StaleWhileRevalidate"/>, how long after expiry an entry may still be served while a background refresh runs.
		/// </summary>
		/// <remarks>
		/// Only applies to the stale-while-revalidate policy. A zero or positive value is required.
		/// The window is evaluated at its endpoints inclusively - an entry that expired exactly the window length ago is still served stale.
		/// </remarks>
		public TimeSpan StaleWhileRevalidateWindow { get; init; } = TimeSpan.Zero;

		/// <summary>
		/// Creates read options for the default policy.
		/// </summary>
		public CacheReadOptions()
		{
			Policy = CacheReadPolicy.Default;
			StaleWhileRevalidateWindow = TimeSpan.Zero;
		}

		/// <summary>
		/// Creates read options for the specified <paramref name="policy"/> and stale-while-revalidate <paramref name="staleWhileRevalidateWindow"/>.
		/// </summary>
		/// <param name="policy">The read policy to apply.</param>
		/// <param name="staleWhileRevalidateWindow">How long after expiry an entry may still be served. Must be non-negative; required when <paramref name="policy"/> is <see cref="CacheReadPolicy.StaleWhileRevalidate"/>.</param>
		/// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="staleWhileRevalidateWindow"/> is negative.</exception>
		/// <exception cref="ArgumentException">Thrown when an undefined <see cref="CacheReadPolicy"/> is supplied.</exception>
		public CacheReadOptions(CacheReadPolicy policy, TimeSpan staleWhileRevalidateWindow)
		{
			if (staleWhileRevalidateWindow < TimeSpan.Zero)
			{
				throw new ArgumentOutOfRangeException(nameof(staleWhileRevalidateWindow), staleWhileRevalidateWindow, "The stale-while-revalidate window must not be negative.");
			}

			if (policy != CacheReadPolicy.Default && policy != CacheReadPolicy.StaleWhileRevalidate)
			{
				throw new ArgumentException($"Undefined cache read policy \"{policy}\".", nameof(policy));
			}

			Policy = policy;
			StaleWhileRevalidateWindow = staleWhileRevalidateWindow;
		}
	}

	/// <summary>
	/// A receipt describing where a value returned by an opt-in read came from.
	/// The receipt is diagnostic only - it never changes the value returned.
	/// </summary>
	/// <param name="Source">Whether the value came from the factory, the first cache layer or a backup layer.</param>
	/// <param name="Freshness">Whether the served value was fresh or stale (including expired-but-revalidating values).</param>
	/// <param name="LayerIndex">The zero-based index of the cache layer that served the value, or <c>-1</c> when served by the value factory.</param>
	public sealed record CacheReadReceipt(CacheValueSource Source, CacheValueFreshness Freshness, int LayerIndex)
	{
		/// <summary>
		/// Whether the value was served by the value factory rather than any cache layer.
		/// </summary>
		public bool IsFromFactory => Source == CacheValueSource.Factory;
		/// <summary>
		/// Whether the value was served by the first (highest priority) cache layer.
		/// </summary>
		public bool IsFromMemoryLayer => Source == CacheValueSource.MemoryLayer;
		/// <summary>
		/// Whether the value was served by a backup (lower priority) cache layer.
		/// </summary>
		public bool IsFromBackupLayer => Source == CacheValueSource.BackupLayer;
		/// <summary>
		/// Whether the served value was fresh.
		/// </summary>
		public bool IsFresh => Freshness == CacheValueFreshness.Fresh;
		/// <summary>
		/// Whether the served value was stale (including a value served stale-while-revalidate after expiry).
		/// </summary>
		public bool IsStale => Freshness == CacheValueFreshness.Stale;
	}

	/// <summary>
	/// The result of an opt-in read: the value plus the <see cref="Receipt"/> describing its source.
	/// </summary>
	/// <typeparam name="T">The type of the cached value.</typeparam>
	/// <param name="Value">The value read from the cache or produced by the value factory.</param>
	/// <param name="Receipt">The source receipt for this read. It does not change <paramref name="Value"/>.</param>
	public sealed record CacheGetResult<T>(T Value, CacheReadReceipt Receipt)
	{
		/// <summary>
		/// Deconstructs the result into its value and receipt.
		/// </summary>
		public void Deconstruct(out T value, out CacheReadReceipt receipt)
		{
			value = Value;
			receipt = Receipt;
		}
	}
}
