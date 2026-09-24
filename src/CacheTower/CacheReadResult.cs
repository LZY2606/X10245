namespace CacheTower;

/// <summary>
/// The result of a cache read performed through <see cref="ICacheStack.GetOrSetWithReceiptAsync{T}(string, System.Func{T, System.Threading.Tasks.Task{T}}, CacheSettings, System.TimeSpan?, System.Threading.CancellationToken)"/>,
/// pairing the value with a <see cref="CacheReadReceipt"/> describing where the value came from.
/// </summary>
/// <typeparam name="T">The type of value in the cache entry.</typeparam>
public readonly record struct CacheReadResult<T>
{
	/// <summary>
	/// The cached or freshly generated value. This is identical to what <see cref="ICacheStack.GetOrSetAsync{T}(string, System.Func{T, System.Threading.Tasks.Task{T}}, CacheSettings)"/> would return for the same read.
	/// </summary>
	public T? Value { get; }

	/// <summary>
	/// The provenance receipt for this read. It never changes <see cref="Value"/>.
	/// </summary>
	public CacheReadReceipt Receipt { get; }

	/// <summary>
	/// Creates a new <see cref="CacheReadResult{T}"/>.
	/// </summary>
	/// <param name="value">The cached or freshly generated value.</param>
	/// <param name="receipt">The provenance receipt for this read.</param>
	public CacheReadResult(T? value, CacheReadReceipt receipt)
	{
		Value = value;
		Receipt = receipt;
	}
}
