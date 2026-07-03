namespace SomeEngine.Job;

public readonly struct JobHandle
{
    internal readonly int Index;
    internal readonly int Version;
    internal readonly long Generation;

    internal JobHandle(int index, int version, long generation)
    {
        Index = index;
        Version = version;
        Generation = generation;
    }

    public bool IsCompleted => JobSystem.IsCompleted(this);

    public void Complete()
    {
        JobSystem.Complete(this);
    }
}



