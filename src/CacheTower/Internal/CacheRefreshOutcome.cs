using System;

namespace CacheTower.Internal;

internal abstract class CacheRefreshOutcome : ICacheEntry
{
	public abstract DateTime Expiry { get; }
	public abstract object? GetValue();
	public CacheReadReceipt Receipt { get; protected set; }

	public static CacheRefreshOutcome<T> FromFactory<T>(CacheEntry<T> entry)
	{
		return new(entry, new(CacheReadSource.Factory, -1));
	}

	public static CacheRefreshOutcome<T> FromCache<T>(CacheEntry<T> entry, CacheReadReceipt receipt)
	{
		return new(entry, receipt);
	}
}

internal sealed class CacheRefreshOutcome<T> : CacheRefreshOutcome
{
	public CacheEntry<T>? Entry { get; }
	public override DateTime Expiry => Entry?.Expiry ?? default;

	public CacheRefreshOutcome(CacheEntry<T>? entry, CacheReadReceipt receipt)
	{
		Entry = entry;
		Receipt = receipt;
	}

	public override object? GetValue() => Entry?.Value;
}
