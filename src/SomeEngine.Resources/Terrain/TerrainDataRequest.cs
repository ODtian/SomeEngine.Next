using System.Runtime.ExceptionServices;
using SomeEngine.Job;

namespace SomeEngine.Resources;

internal abstract class TerrainDataRequest(long sequence, JobHandle dependency, TerrainCompletion completion)
{
    private int _submitted;
    internal long Sequence { get; } = sequence;
    internal JobHandle Dependency { get; } = dependency;
    internal TerrainCompletion Completion { get; } = completion;
    internal bool IsSubmitted => Volatile.Read(ref _submitted) != 0;
    internal void AcceptSubmission() => Volatile.Write(ref _submitted, 1);
}

/// <summary>Bridges an external completion and its result into the borrowed job scheduler.</summary>
internal sealed class TerrainCompletion : IJobExternalFence
{
    private readonly TaskCompletionSource _signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ExceptionDispatchInfo? _failure;

    internal TerrainCompletion(Scheduler scheduler, ReadOnlySpan<JobResourceAccess> accesses = default)
    {
        JobHandle fence = scheduler.CreateExternalFenceHandle(this, accesses);
        Handle = scheduler.Schedule(new CompleteWork(this), [], fence);
    }

    internal JobHandle Handle { get; }
    public bool IsSignaled => _signal.Task.IsCompleted;

    public void OnSignaled(Action<object?> callback, object? state)
    {
        if (IsSignaled) callback(state);
        else _signal.Task.GetAwaiter().OnCompleted(() => callback(state));
    }

    internal void Complete() => _signal.TrySetResult();

    internal void Fail(Exception failure)
    {
        _failure = ExceptionDispatchInfo.Capture(failure);
        _signal.TrySetResult();
    }

    private readonly struct CompleteWork(TerrainCompletion completion) : IJob
    {
        public void Execute() => completion._failure?.Throw();
    }
}
