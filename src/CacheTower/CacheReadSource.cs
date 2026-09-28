using System;

namespace CacheTower
{
	/// <summary>
	/// Describes where the value returned by an opt-in stale-while-revalidate read originated from.
	/// </summary>
	public enum CacheReadSource
	{
		/// <summary>
		/// The source of the value could not be determined. A read that cannot be resolved to a definite source returns this
		/// explicitly rather than guessing a successful source.
		/// </summary>
		Unknown = 0,
		/// <summary>
		/// A fresh (unexpired) value was found in the first (highest priority) cache layer.
		/// </summary>
		MemoryLayer = 1,
		/// <summary>
		/// A fresh (unexpired) value was found in a lower priority cache layer.
		/// </summary>
		FallbackLayer = 2,
		/// <summary>
		/// An expired value was served because the read occurred within the configured stale-while-revalidate window.
		/// A single-flight refresh of the key was triggered (or joined) by the read.
		/// </summary>
		Stale = 3,
		/// <summary>
		/// The value was produced by a shared, single-flight refresh started by another caller for the same key.
		/// </summary>
		Fresh = 4,
		/// <summary>
		/// The value was produced by the value factory because this caller owned the foreground single-flight refresh for the key.
		/// </summary>
		Factory = 5
	}
}
