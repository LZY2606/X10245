using System;
using System.Threading;
using System.Threading.Tasks;

namespace CacheTower.Internal;

internal static class TaskExtensions
{
	/// <summary>
	/// Allows the <em>caller</em> to stop waiting on the given <paramref name="task"/> when the
	/// <paramref name="cancellationToken"/> is cancelled. The underlying task itself is never
	/// cancelled and continues to run to completion - this is what keeps a shared (single-flight)
	/// refresh alive even when an individual caller gives up.
	/// </summary>
	public static async Task<T> AwaitWithCallerCancellation<T>(this Task<T> task, CancellationToken cancellationToken)
	{
		if (!cancellationToken.CanBeCanceled || task.IsCompleted)
		{
			return await task.ConfigureAwait(false);
		}

		var cancellationSignal = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
		using (cancellationToken.Register(static state => ((TaskCompletionSource<object?>)state!).TrySetResult(null), cancellationSignal))
		{
			var completedTask = await Task.WhenAny(task, cancellationSignal.Task).ConfigureAwait(false);
			if (completedTask != task)
			{
				throw new OperationCanceledException(cancellationToken);
			}
		}

		return await task.ConfigureAwait(false);
	}
}
