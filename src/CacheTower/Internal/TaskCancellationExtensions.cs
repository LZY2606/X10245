using System.Threading;
using System.Threading.Tasks;

namespace CacheTower.Internal
{
	internal static class TaskCancellationExtensions
	{
		/// <summary>
		/// Awaits <paramref name="task"/> without registering the caller's cancellation against it.
		/// Cancelling the returned await only abandons the wait - the underlying shared refresh keeps running to completion.
		/// </summary>
		public static async Task<T> WithCancellation<T>(this Task<T> task, CancellationToken cancellationToken)
		{
			var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
			using (cancellationToken.Register(state => ((TaskCompletionSource<bool>)state!).TrySetResult(true), tcs))
			{
				var completed = await Task.WhenAny(task, tcs.Task).ConfigureAwait(false);
				if (ReferenceEquals(completed, task))
				{
					return await task.ConfigureAwait(false);
				}
			}

			//The caller has abandoned the wait; observe the shared task separately so it cannot raise an unobserved exception.
			_ = ObserveAsync(task);
			throw new TaskCanceledException(task);

			static async Task ObserveAsync(Task<T> sharedTask)
			{
				try
				{
					await sharedTask.ConfigureAwait(false);
				}
				catch
				{
					//Swallowed: the original refresh owner is responsible for surfacing factory failures.
				}
			}
		}
	}
}
