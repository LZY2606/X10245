using System;
using System.Threading;

namespace CacheTower;

public enum CacheReadMode
{
	Default = 0,
	StaleWhileRevalidate = 1
}

public enum CacheReadSource
{
	MemoryLayer = 0,
	BackingLayer = 1,
	Factory = 2
}

public enum CacheReadFreshness
{
	Fresh = 0,
	Stale = 1
}

public sealed record CacheReadReceipt(
	CacheReadSource Source,
	CacheReadFreshness? Freshness,
	int LayerIndex
)
{
	public static CacheReadReceipt FromFactory() => new(CacheReadSource.Factory, null, 0);

	public static CacheReadReceipt FromLayer(int layerIndex, CacheReadFreshness freshness)
	{
		var source = layerIndex == 0 ? CacheReadSource.MemoryLayer : CacheReadSource.BackingLayer;
		return new(source, freshness, layerIndex);
	}
}

public sealed record CacheReadResult<T>(T? Value, CacheReadReceipt Receipt);

public readonly record struct CacheReadOptions
{
	public CacheSettings Settings { get; }
	public CacheReadMode Mode { get; }
	public TimeSpan? StaleWhileRevalidateWindow { get; }
	public CancellationToken CancellationToken { get; }

	public CacheReadOptions(CacheSettings settings, CancellationToken cancellationToken = default)
	{
		Settings = settings;
		Mode = CacheReadMode.Default;
		StaleWhileRevalidateWindow = null;
		CancellationToken = cancellationToken;
	}

	public CacheReadOptions(
		CacheSettings settings,
		TimeSpan staleWhileRevalidateWindow,
		CancellationToken cancellationToken = default
	)
	{
		if (staleWhileRevalidateWindow < TimeSpan.Zero)
		{
			throw new ArgumentOutOfRangeException(nameof(staleWhileRevalidateWindow), "The stale-while-revalidate window cannot be negative.");
		}

		Settings = settings;
		Mode = CacheReadMode.StaleWhileRevalidate;
		StaleWhileRevalidateWindow = staleWhileRevalidateWindow;
		CancellationToken = cancellationToken;
	}

	public CacheReadOptions(
		CacheSettings settings,
		CacheReadMode mode,
		TimeSpan? staleWhileRevalidateWindow = null,
		CancellationToken cancellationToken = default
	)
	{
		if (mode < CacheReadMode.Default || mode > CacheReadMode.StaleWhileRevalidate)
		{
			throw new ArgumentOutOfRangeException(nameof(mode), "Unknown cache read mode.");
		}

		if (staleWhileRevalidateWindow.HasValue && staleWhileRevalidateWindow.GetValueOrDefault() < TimeSpan.Zero)
		{
			throw new ArgumentOutOfRangeException(nameof(staleWhileRevalidateWindow), "The stale-while-revalidate window cannot be negative.");
		}

		if (mode == CacheReadMode.StaleWhileRevalidate && !staleWhileRevalidateWindow.HasValue)
		{
			throw new ArgumentException("A stale-while-revalidate read requires a window.", nameof(staleWhileRevalidateWindow));
		}

		if (mode == CacheReadMode.Default && staleWhileRevalidateWindow.HasValue)
		{
			throw new ArgumentException("A stale-while-revalidate window cannot be specified for a default read.", nameof(staleWhileRevalidateWindow));
		}

		Settings = settings;
		Mode = mode;
		StaleWhileRevalidateWindow = staleWhileRevalidateWindow;
		CancellationToken = cancellationToken;
	}
}
