namespace CacheTower;

/// <summary>
/// Contains the value returned by a cache read and a receipt describing its source.
/// </summary>
/// <typeparam name="T">The type of the cached value.</typeparam>
public readonly record struct CacheReadResult<T>
{
	/// <summary>
	/// The value returned by the cache read.
	/// </summary>
	public T? Value { get; }

	/// <summary>
	/// Describes where <see cref="Value"/> came from.
	/// </summary>
	public CacheReadReceipt Receipt { get; }

	/// <summary>
	/// Creates a new cache read result.
	/// </summary>
	/// <param name="value">The value returned to the caller.</param>
	/// <param name="receipt">The source receipt.</param>
	public CacheReadResult(T? value, CacheReadReceipt receipt)
	{
		Value = value;
		Receipt = receipt;
	}
}
