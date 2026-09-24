using System;
using System.Threading;
using System.Threading.Tasks;

namespace CacheTower.Internal;

internal static class TaskExtensions
{
	/// <summary>
	/// Awaits <paramref name="task"/> but throws <see cref="OperationCanceledException"/> for the awaiting caller when <paramref name="cancellationToken"/> is cancelled.
	/// The underlying task itself is never cancelled and keeps running to completion - this is what keeps a shared (single-flight) refresh alive when an individual caller cancels.
	/// </summary>
	public static async Task<T> WithCancellation<T>(this Task<T> task, CancellationToken cancellationToken)
	{
		if (!cancellationToken.CanBeCanceled || task.IsCompleted)
		{
			return await task.ConfigureAwait(false);
		}

		var cancellationSource = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
		using (cancellationToken.Register(static state => ((TaskCompletionSource<object?>)state!).TrySetResult(null), cancellationSource))
		{
			if (await Task.WhenAny(task, cancellationSource.Task).ConfigureAwait(false) != task)
			{
				throw new OperationCanceledException(cancellationToken);
			}
		}

		return await task.ConfigureAwait(false);
	}
}
