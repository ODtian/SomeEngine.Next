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
    internal virtual void Cancel()
    {
        if (IsSubmitted) return;
        AcceptSubmission();
        Completion.Cancel();
    }
}

/// <summary>Bridges an external completion and its result into the borrowed job scheduler.</summary>
internal sealed class TerrainCompletion
{
    private readonly TaskCompletionSource _signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Scheduler _scheduler;
    private readonly JobHandleAwaiter _sourceResult;
    private Action? _work;
    private Action? _cleanup;

    internal TerrainCompletion(Scheduler scheduler, ReadOnlySpan<JobResourceAccess> accesses = default)
    {
        _scheduler = scheduler;
        JobHandle ready = scheduler.CreateExternalFenceHandle(_signal.Task, accesses);
        _sourceResult = ready.GetAwaiter();
        Handle = scheduler.ScheduleFinally(new CompleteWork(this), accesses, default, ready);
    }

    internal JobHandle Handle { get; }
    internal bool IsSignaled => _signal.Task.IsCompleted;
    internal void SetCompletionWork(Action work, Action? cleanup)
    {
        _work = work;
        _cleanup = cleanup;
    }

    internal void Complete() => _signal.TrySetResult();

    internal void Fail(Exception failure)
    {
        _signal.TrySetException(failure);
    }

    internal void Cancel()
    {
        Fail(new OperationCanceledException("Terrain work ended before graphics submission."));
        // Observe shutdown's result through a dependent handle, retaining cancellation on the caller's handle.
        try { _scheduler.CombineDependencies([Handle]).Complete(); }
        catch (OperationCanceledException) { }
    }

    private readonly struct CompleteWork(TerrainCompletion completion) : IJob
    {
        public void Execute()
        {
            try
            {
                completion._sourceResult.GetResult();
                completion._work?.Invoke();
            }
            finally
            {
                Action? cleanup = completion._cleanup;
                completion._work = null;
                completion._cleanup = null;
                cleanup?.Invoke();
            }
        }
    }
}
