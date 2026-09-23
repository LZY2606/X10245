using System;

namespace CacheTower.Internal;

internal sealed class CacheRefreshCompletion : ICacheEntry
{
	public CacheRefreshCompletion(object? value, DateTime expiry, bool cached, CacheReadReceipt receipt)
	{
		Value = value;
		Expiry = expiry;
		Cached = cached;
		Receipt = receipt;
	}

	public object? Value { get; }
	public DateTime Expiry { get; }
	public bool Cached { get; }
	public CacheReadReceipt Receipt { get; }
}
