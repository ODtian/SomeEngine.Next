namespace SomeEngine.Job;

public readonly ref struct EventWriter<T> where T : unmanaged
{
    private readonly SynchronousResourceAccess _access;

    internal EventWriter(Span<T> values, EventWriteRange range, SynchronousResourceAccess access = default)
    {
        Values = values;
        Range = range;
        _access = access;
    }

    public Span<T> Values { get; }
    public EventWriteRange Range { get; }
    public void Dispose() => _access.Dispose();
}
