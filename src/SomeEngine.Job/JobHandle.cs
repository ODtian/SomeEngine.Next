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
}



