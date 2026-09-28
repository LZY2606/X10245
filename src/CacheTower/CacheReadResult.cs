using System;

namespace CacheTower
{
	/// <summary>
	/// A receipt describing the provenance of a value returned by a stale-while-revalidate read.
	/// </summary>
	/// <remarks>
	/// The receipt is metadata for a read - it never wraps, modifies or replaces the value that would have been returned.
	/// </remarks>
	/// <param name="Source">Where the returned value originated from.</param>
	/// <param name="LayerIndex">
	/// The index of the cache layer the value came from, or <c>-1</c> when the value did not come from a cache layer.
	/// </param>
	/// <param name="ServedExpiry">The expiry date of the entry that was served, or <see langword="null"/> when no entry was served.</param>
	/// <param name="RefreshTriggered">Whether this read triggered, or joined, a single-flight refresh for the key.</param>
	public readonly record struct CacheReadReceipt(
		CacheReadSource Source,
		int LayerIndex,
		DateTime? ServedExpiry,
		bool RefreshTriggered
	)
	{
		/// <summary>
		/// A receipt for a read whose source could not be determined.
		/// </summary>
		public static CacheReadReceipt Unknown { get; } = new(CacheReadSource.Unknown, -1, null, false);
	}

	/// <summary>
	/// The result of a stale-while-revalidate read: the value exactly as it would be returned without a receipt, plus a
	/// <see cref="Receipt"/> describing where the value came from.
	/// </summary>
	/// <typeparam name="T">The type of the cached value.</typeparam>
	/// <param name="Value">The cached value.</param>
	/// <param name="Receipt">The provenance receipt for this read.</param>
	public readonly record struct CacheReadResult<T>(T? Value, CacheReadReceipt Receipt)
	{
		/// <summary>
		/// The cached value.
		/// </summary>
		public T? Value { get; init; } = Value;

		/// <summary>
		/// The provenance receipt for this read.
		/// </summary>
		public CacheReadReceipt Receipt { get; init; } = Receipt;
	}
}
