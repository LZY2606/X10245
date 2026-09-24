namespace CacheTower
{
	/// <summary>
	/// Controls how <see cref="ICacheStack.GetOrSetWithReceiptAsync{T}(string, System.Func{T, System.Threading.Tasks.Task{T}}, CacheSettings, CacheReadPolicy, System.Threading.CancellationToken)"/>
	/// treats an entry that has passed its expiry.
	/// </summary>
	/// <remarks>
	/// The read policy never changes the value returned for an unexpired entry or for a cache miss - it only controls how entries that are past their <see cref="CacheEntry{T}.Expiry"/> are handled.
	/// </remarks>
	public enum CacheReadPolicy
	{
		/// <summary>
		/// The default policy. An expired entry is never returned - a foreground refresh is required to satisfy the read.
		/// </summary>
		Default = 0,
		/// <summary>
		/// Opt-in stale-while-revalidate. An entry that expired within the window configured by
		/// <see cref="CacheSettings.StaleWhileRevalidate"/> is served once as stale while a single shared refresh is started in the background.
		/// </summary>
		StaleWhileRevalidate = 1
	}
}
