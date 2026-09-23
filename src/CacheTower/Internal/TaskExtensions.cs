using System;
using System.Threading;
using System.Threading.Tasks;

namespace CacheTower.Internal;

internal static class TaskExtensions
{
	/// <summary>
	/// Awaits the <paramref name="task"/> but stops waiting when the <paramref name="cancellationToken"/> is cancelled.
	/// The underlying task itself is never cancelled and continues running in the background.
	/// </summary>
	public static async Task<T> AwaitCancellation<T>(this Task<T> task, CancellationToken cancellationToken)
	{
		if (!cancellationToken.CanBeCanceled || task.IsCompleted)
		{
			return await task.ConfigureAwait(false);
		}

		var cancellationSource = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
		using (cancellationToken.Register(() => cancellationSource.TrySetResult(null)))
		{
			if (await Task.WhenAny(task, cancellationSource.Task).ConfigureAwait(false) != task)
			{
				throw new OperationCanceledException(cancellationToken);
			}
		}

		return await task.ConfigureAwait(false);
	}
}
