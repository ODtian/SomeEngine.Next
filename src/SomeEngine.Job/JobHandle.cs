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
        JobHandleAwaiter completion = this.GetAwaiter();
        if (completion.IsCompleted)
        {
            try
            {
                completion.GetResult();
                return ValueTask.CompletedTask;
            }
            catch (Exception error)
            {
                return ValueTask.FromException(error);
            }
        }
        var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        completion.OnCompleted(() =>
        {
            try { completion.GetResult(); result.SetResult(); }
            catch (Exception error) { result.SetException(error); }
        });
        return new ValueTask(result.Task);
    }
}



