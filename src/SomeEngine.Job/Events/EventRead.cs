namespace SomeEngine.Job;

public readonly ref struct EventRead<T> where T : unmanaged
{
    private readonly EventLog<T>? _log;
    private readonly SynchronousResourceAccess _access;

    internal EventRead(EventLog<T> log, ReadOnlySpan<T> values, ulong through,
        SynchronousResourceAccess access = default)
    {
        _log = log;
        First = values;
        Through = through;
        _access = access;
    }

    public ReadOnlySpan<T> First { get; }
    public ReadOnlySpan<T> Second => default;
    public ulong Through { get; }
    public int Count => First.Length;
    public ReadOnlySpan<T>.Enumerator GetEnumerator() => First.GetEnumerator();

    public void Dispose()
    {
        _log?.EndRead();
        _access.Dispose();
    }
}
