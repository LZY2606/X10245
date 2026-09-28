namespace CacheTower
{
	/// <summary>
	/// The result of a cache read, pairing the resolved <typeparamref name="TValue"/> with a <see cref="CacheSourceReceipt"/> describing its provenance.
	/// </summary>
	/// <typeparam name="TValue">The type of value returned from the cache read.</typeparam>
	/// <remarks>
	/// The receipt never alters the value - it is metadata describing where and in what state the value was resolved.
	/// </remarks>
	/// <param name="Value">The value resolved from the cache layers or the value factory.</param>
	/// <param name="Receipt">The receipt describing the origin and freshness of <paramref name="Value"/>.</param>
	public sealed record CacheReadResult<TValue>(TValue? Value, CacheSourceReceipt Receipt)
	{
		/// <summary>
		/// Converts the read result to its <see cref="Value"/>, allowing it to be used wherever just the value is required.
		/// </summary>
		/// <param name="result">The read result to convert.</param>
		public static implicit operator TValue?(CacheReadResult<TValue>? result) => result != null ? result.Value : default;
	}
}
