namespace SomeEngine.Job;

public sealed class EventReader<T> : IDisposable where T : unmanaged
{
    private readonly EventLog<T> _log;
    private EventReadRange _read;
    private bool _hasRead;
    internal int Index;
    internal bool Disposed;

    internal EventReader(EventLog<T> log, int index) => (_log, Index) = (log, index);

    public ulong Position => _log.GetPosition(this);
    public JobHandle Ready => CurrentRead.Ready;
    public JobResourceAccess Read => CurrentRead.Read;
    public bool IsDisposed => Disposed;

    /// <summary>Starts one observation of the published interval. Its bounds stay fixed across Jobs.</summary>
    public EventReadRange Capture()
    {
        _read = _log.Capture(this);
        _hasRead = true;
        return _read;
    }

    private EventReadRange CurrentRead => _hasRead ? _read : Capture();
    public EventRead<T> Open() => Open(CurrentRead);
    public EventRead<T> Open(scoped in EventReadRange range) => _log.Open(this, range, default);
    internal EventRead<T> Open(scoped in EventReadRange range, JobHandle access) => _log.Open(this, range, access);
    internal EventRead<T> Open(JobHandle access) => _log.Open(this, CurrentRead, access);

    public void Advance(ulong through)
    {
        _log.SetPosition(this, through, restore: false);
        _hasRead = false;
    }

    /// <summary>Restores this consumer after the log's retained data has been restored.</summary>
    public void Reset(ulong position)
    {
        _log.SetPosition(this, position, restore: true);
        _hasRead = false;
    }

    public void Dispose()
    {
        if (Disposed) return;
        _log.RemoveReader(this);
        _hasRead = false;
    }

    internal void ClearRead() => _hasRead = false;
}
