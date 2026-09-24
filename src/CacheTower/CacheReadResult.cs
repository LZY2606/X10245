namespace CacheTower;

/// <summary>
/// The result of a cache read that pairs the value with a <see cref="CacheTower.CacheReadReceipt"/> describing where the value came from.
/// </summary>
/// <typeparam name="T">The type of value that was read.</typeparam>
public readonly record struct CacheReadResult<T>
{
	/// <summary>
	/// The value that was read. This is the same value a receipt-less read would have returned.
	/// </summary>
	public T? Value { get; }

	/// <summary>
	/// The receipt describing the origin of <see cref="Value"/>.
	/// </summary>
	public CacheReadReceipt Receipt { get; }

	/// <summary>
	/// Creates a new <see cref="CacheReadResult{T}"/>.
	/// </summary>
	/// <param name="value">The value that was read.</param>
	/// <param name="receipt">The receipt describing the origin of <paramref name="value"/>.</param>
	public CacheReadResult(T? value, CacheReadReceipt receipt)
	{
		Value = value;
		Receipt = receipt;
	}
}
