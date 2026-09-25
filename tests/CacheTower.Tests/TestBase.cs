using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CacheTower.Tests.Utils;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CacheTower.Tests
{
	public abstract class TestBase
	{
		/// <summary>
		/// Polls the <paramref name="condition"/> until it holds, failing with the given context when it does not
		/// (so a violation reports where and why instead of a bare true/false).
		/// </summary>
		protected static async Task WaitForConditionAsync(Func<bool> condition, string description, int timeoutMilliseconds = 10000)
		{
			var stopwatch = System.Diagnostics.Stopwatch.StartNew();
			while (!condition())
			{
				if (stopwatch.ElapsedMilliseconds > timeoutMilliseconds)
				{
					Assert.Fail($"Timed out after {timeoutMilliseconds}ms waiting for: {description}");
				}

				await Task.Delay(10).ConfigureAwait(false);
			}
		}

		/// <summary>
		/// Starts <paramref name="readerCount"/> concurrent receipt reads against the same key, released together by a barrier.
		/// </summary>
		protected static async Task<CacheReadReceipt<string>[]> RunConcurrentReadsAsync(CacheStack cacheStack, string cacheKey, ControlledValueFactory factory, CacheSettings settings, int readerCount)
		{
			using var barrier = new Barrier(readerCount);
			var receipts = new CacheReadReceipt<string>[readerCount];
			var readers = Enumerable.Range(0, readerCount).Select(i => Task.Run(async () =>
			{
				barrier.SignalAndWait();
				receipts[i] = await cacheStack.GetOrSetWithReceiptAsync(cacheKey, factory.AsFunc, settings).ConfigureAwait(false);
			})).ToArray();
			await Task.WhenAll(readers).ConfigureAwait(false);
			return receipts;
		}

		/// <summary>
		/// Issues stale-while-revalidate reads (each must be served stale) until the factory reaches
		/// <paramref name="expectedCallCount"/> - proving a failed background refresh released the key lock.
		/// </summary>
		protected static async Task PollStaleReadsUntilFactoryCallAsync(CacheStack cacheStack, string cacheKey, ControlledValueFactory factory, CacheSettings settings, int expectedCallCount, string expectedValue, string context)
		{
			var timeout = DateTime.UtcNow.AddSeconds(10);
			while (factory.CallCount < expectedCallCount)
			{
				var pollReceipt = await cacheStack.GetOrSetWithReceiptAsync(cacheKey, factory.AsFunc, settings).ConfigureAwait(false);
				if (pollReceipt.Source != CacheValueSource.Stale || pollReceipt.Value != expectedValue)
				{
					Assert.Fail($"Poll at {context}: expected (Stale, \"{expectedValue}\"), actual ({pollReceipt.Source}, \"{pollReceipt.Value}\").");
				}

				if (DateTime.UtcNow > timeout)
				{
					Assert.Fail($"Timed out waiting for factory call #{expectedCallCount} at {context} (calls so far: {factory.CallCount}). A failed refresh may not have released the key lock.");
				}

				await Task.Delay(10).ConfigureAwait(false);
			}
		}
	}
}
