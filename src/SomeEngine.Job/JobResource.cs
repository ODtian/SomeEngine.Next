namespace SomeEngine.Job;

public readonly struct JobResource
{
    internal readonly int Id;
    internal readonly int Version;
    internal readonly long Generation;

    internal JobResource(int id, int version, long generation)
    {
        Id = id;
        Version = version;
        Generation = generation;
    }
}



