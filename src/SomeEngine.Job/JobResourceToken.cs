namespace SomeEngine.Job;

public readonly struct JobResourceToken
{
    internal readonly int Id;
    internal readonly int Version;
    internal readonly long Generation;

    internal JobResourceToken(int id, int version, long generation)
    {
        Id = id;
        Version = version;
        Generation = generation;
    }
}



