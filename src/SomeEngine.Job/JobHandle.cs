namespace SomeEngine.Job;

public readonly struct JobHandle
{
    internal readonly Scheduler? Scheduler;
    internal readonly int Index;
    internal readonly int Version;
    internal readonly long Generation;

    internal JobHandle(int index, int version, long generation, Scheduler scheduler)
    {
        Scheduler = scheduler;
        Index = index;
        Version = version;
        Generation = generation;
    }

    public bool IsCompleted => Scheduler?.IsCompleted(this) ?? true;

    public void Complete()
    {
        Scheduler?.Complete(this);
    }

    /// <summary>Waits for the work and its descendants, propagating their completion result.</summary>
    public ValueTask WaitAsync()
    {
        if (IsCompleted)
        {
            Complete();
            return ValueTask.CompletedTask;
        }
        var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        JobSystem.OnCompleted(this, static (handle, state) =>
        {
            var result = (TaskCompletionSource)state!;
            try { handle.Complete(); result.SetResult(); }
            catch (Exception error) { result.SetException(error); }
        }, result);
        return new ValueTask(result.Task);
    }
}



