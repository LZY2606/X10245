using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CacheTower.Tests.Utils;

/// <summary>
/// A value factory with controllable per-call behaviour: every call is counted and signalled,
/// individual calls can be paused behind a gate (to orchestrate races deterministically) and the
/// produced value (or injected failure) is decided per call number by the test.
/// </summary>
internal sealed class ControlledValueFactory
{
	private readonly Func<int, string?, string> resultSelector;
	private readonly ConcurrentDictionary<int, TaskCompletionSource> callStartedSignals = new();
	private readonly ConcurrentDictionary<int, TaskCompletionSource> callGates = new();
	private int callCount;

	public ControlledValueFactory(Func<int, string?, string> resultSelector)
	{
		this.resultSelector = resultSelector;
	}

	/// <summary>How many times the factory has been invoked.</summary>
	public int CallCount => Volatile.Read(ref callCount);

	/// <summary>The factory function to pass to the cache stack.</summary>
	public Func<string, Task<string>> AsFunc => InvokeAsync;

	private async Task<string> InvokeAsync(string oldValue)
	{
		var callNumber = Interlocked.Increment(ref callCount);
		if (callStartedSignals.TryGetValue(callNumber, out var started))
		{
			started.TrySetResult();
		}

		if (callGates.TryGetValue(callNumber, out var gate))
		{
			await gate.Task.ConfigureAwait(false);
		}

		return resultSelector(callNumber, oldValue);
	}

	/// <summary>Pauses the given call number behind a gate until <see cref="OpenGate"/> is called. Register before the call can happen.</summary>
	public void CloseGate(int callNumber)
	{
		callGates.GetOrAdd(callNumber, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
	}

	/// <summary>Releases a gated call so it can complete.</summary>
	public void OpenGate(int callNumber)
	{
		if (callGates.TryGetValue(callNumber, out var gate))
		{
			gate.TrySetResult();
		}
	}

	/// <summary>Waits until the given call number has started, failing with context on timeout.</summary>
	public async Task WaitForCallAsync(int callNumber, int timeoutMilliseconds = 10000)
	{
		var signal = callStartedSignals.GetOrAdd(callNumber, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
		if (await Task.WhenAny(signal, Task.Delay(timeoutMilliseconds)).ConfigureAwait(false) != signal)
		{
			Assert.Fail($"Timed out after {timeoutMilliseconds}ms waiting for factory call #{callNumber} (calls so far: {CallCount}).");
		}
	}
}
