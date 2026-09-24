namespace CacheTower;

/// <summary>
/// Contains a read value and a receipt describing where that value came from.
/// </summary>
/// <typeparam name="T">The type of the read value.</typeparam>
/// <param name="Value">The value returned to the caller.</param>
/// <param name="Receipt">The source receipt for this read.</param>
public readonly record struct CacheReadResult<T>(T Value, CacheReadReceipt Receipt)
{
	/// <summary>
	/// Implicitly converts a read result to its value.
	/// </summary>
	/// <param name="result">The read result.</param>
	public static implicit operator T(CacheReadResult<T> result) => result.Value;
}
