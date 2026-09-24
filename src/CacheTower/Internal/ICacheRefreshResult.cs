using System.Threading.Tasks;

namespace CacheTower.Internal;

internal interface ICacheRefreshResult
{
	ICacheEntry CacheEntry { get; }
	bool ServedFromFactory { get; }
	int LayerIndex { get; }
}

internal sealed record CacheRefreshResult<T>(CacheEntry<T> CacheEntry, bool ServedFromFactory, int LayerIndex) : ICacheRefreshResult
{
	ICacheEntry ICacheRefreshResult.CacheEntry => CacheEntry;
}
