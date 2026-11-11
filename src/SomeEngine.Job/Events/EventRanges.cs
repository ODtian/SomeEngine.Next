namespace SomeEngine.Job;

/// <summary>A published event interval and the access needed to read it.</summary>
public readonly struct EventReadRange
{
    internal EventReadRange(ulong first, ulong through, JobHandle ready, JobResourceAccess read)
        => (First, Through, Ready, Read) = (first, through, ready, read);

    public ulong First { get; }
    public ulong Through { get; }
    public JobHandle Ready { get; }
    public JobResourceAccess Read { get; }
}

/// <summary>An event interval assigned to a producer, with its storage completion and write access.</summary>
public readonly struct EventWriteRange
{
    internal EventWriteRange(ulong first, ulong through, JobHandle ready, JobResourceAccess write)
        => (First, Through, Ready, Write) = (first, through, ready, write);

    public ulong First { get; }
    public ulong Through { get; }
    public JobHandle Ready { get; }
    public JobResourceAccess Write { get; }
}
