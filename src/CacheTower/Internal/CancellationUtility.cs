using System;
using System.Threading;
using System.Threading.Tasks;

namespace CacheTower.Internal;

internal static class CancellationUtility
{
	/// <summary>
	/// Awaits <paramref name="task"/> while honoring <paramref name="cancellationToken"/> for the caller only.
	/// </summary>
	/// <remarks>
	/// Cancelling the token never cancels the underlying task - it continues to run to completion.
	/// This is used so a caller cancelling their read does not cancel a refresh shared with other callers.
	/// </remarks>
	public static async Task<T> WaitWithCancellationAsync<T>(Task<T> task, CancellationToken cancellationToken)
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
