using System.Collections.Concurrent;

namespace SomeEngine.Job.Tests;

internal sealed class ExecutionEvents
{
    private readonly ConcurrentQueue<JobSignal> _signals = new();
    internal JobSignal CreateSignal() => new(this);
    internal void Record(JobSignal signal) => _signals.Enqueue(signal);
    internal void AssertBefore(JobSignal first, JobSignal second)
    {
        JobSignal[] signals = _signals.ToArray();
        int firstIndex = Array.IndexOf(signals, first);
        int secondIndex = Array.IndexOf(signals, second);
        Assert.True(firstIndex >= 0, "The first execution point was reached.");
        Assert.True(secondIndex > firstIndex, "The second execution point follows the first.");
    }
}

internal sealed class JobSignal(ExecutionEvents events) : IDisposable
{
    private readonly ManualResetEventSlim _signal = new();
    private int _set;
    internal bool IsSet => _signal.IsSet;
    internal void Set()
    {
        if (Interlocked.Exchange(ref _set, 1) == 0) events.Record(this);
        _signal.Set();
    }
    internal void Wait() => Assert.True(_signal.Wait(TimeSpan.FromSeconds(10)));
    internal bool Wait(int milliseconds) => _signal.Wait(milliseconds);
    internal bool Wait(TimeSpan timeout) => _signal.Wait(timeout);
    public void Dispose() => _signal.Dispose();
}
