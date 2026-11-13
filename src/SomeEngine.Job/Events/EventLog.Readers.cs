using SomeEngine.Core.Diagnostics;

namespace SomeEngine.Job;

public sealed partial class EventLog<T> where T : unmanaged
{
    public EventReader<T> Watch() { lock (_gate) { CheckActive(); return AddReader(Guid.Empty, _through); } }

    public EventReader<T> Watch(Guid persistentId)
    {
        lock (_gate)
        {
            CheckActive();
            DevelopmentCheck.Require(persistentId != Guid.Empty, "A named event reader requires its identity.");
            _named ??= [];
            if (_named.TryGetValue(persistentId, out EventReader<T>? reader)) return reader;
            reader = AddReader(persistentId, _through);
            _named.Add(persistentId, reader);
            return reader;
        }
    }

    private EventReader<T> AddReader(Guid id, ulong position)
    {
        if (_readers.Length == _readerCount) Array.Resize(ref _readers, Math.Max(4, _readerCount * 2));
        var reader = new EventReader<T>(this, _readerCount);
        _readers[_readerCount++] = new ReaderEntry
        { Id = id, Position = position, LastAdvance = _stepCount, Reader = reader };
        return reader;
    }

    private void CheckReader(EventReader<T> reader)
    {
        CheckActive();
        DevelopmentCheck.Require(!reader.Disposed, "The event reader has been disposed.");
    }

    internal ulong GetPosition(EventReader<T> reader)
    { lock (_gate) { CheckReader(reader); return _readers[reader.Index].Position; } }

    internal EventReadRange Capture(EventReader<T> reader)
    {
        lock (_gate)
        {
            CheckReader(reader);
            ulong position = _readers[reader.Index].Position;
            return new EventReadRange(position, _through, _ready, _scheduler is null ? default
                : JobResourceAccess.Read(_storage).WithRange((long)position, (long)(_through - position)));
        }
    }

    internal EventRead<T> Open(EventReader<T> reader, scoped in EventReadRange range, JobHandle access)
    {
        if (_scheduler is not null && access.Index == 0 && _scheduler.GetCurrentScope().Index == 0)
            range.Ready.Complete();
        SynchronousResourceAccess acquired = EnterAccess(JobAccessMode.Read, range.First, range.Through, whole: false, access);
        try
        {
            lock (_gate)
            {
                CheckReader(reader);
                DevelopmentCheck.Require(range.First >= _first && range.Through <= _through,
                    "The event read interval belongs to the retained published events.");
                if (_scheduler is not null)
                    DevelopmentCheck.Require(range.Read.CoversIdentityAndMode(JobResourceAccess.Read(_storage)),
                        "The event read interval belongs to this log.");
                _openReads++;
                return new EventRead<T>(this, _values.AsSpan(_start + (int)(range.First - _first),
                    (int)(range.Through - range.First)), range.Through, acquired);
            }
        }
        catch { acquired.Dispose(); throw; }
    }

    internal void EndRead() { lock (_gate) _openReads--; }

    internal void SetPosition(EventReader<T> reader, ulong position, bool restore)
    {
        lock (_gate)
        {
            CheckReader(reader);
            ref ReaderEntry entry = ref _readers[reader.Index];
            DevelopmentCheck.Require(position >= _first && position <= _through && (restore || position >= entry.Position),
                "The event reader advances within its retained published interval.");
            if (position != entry.Position) entry.LastAdvance = _stepCount;
            entry.Position = position;
        }
    }

    internal void RemoveReader(EventReader<T> reader)
    {
        lock (_gate)
        {
            CheckReader(reader);
            int index = reader.Index;
            Guid id = _readers[index].Id;
            if (id != Guid.Empty) _named!.Remove(id);
            int remaining = --_readerCount - index;
            _readers.AsSpan(index + 1, remaining).CopyTo(_readers.AsSpan(index));
            for (int i = index; i < _readerCount; i++) _readers[i].Reader.Index = i;
            _readers[_readerCount] = default;
            reader.Disposed = true;
        }
    }

    public EventLogData<T> CaptureData(EventLogData<T>? target = null) => CaptureData(target, default);

    internal EventLogData<T> CaptureData(EventLogData<T>? target, JobHandle access)
    {
        using SynchronousResourceAccess acquired = EnterAccess(JobAccessMode.Read, 0, 0, whole: true, access);
        lock (_gate)
        {
            CheckActive();
            target ??= new();
            ulong first = _through;
            for (int i = 0; i < _readerCount; i++) first = Math.Min(first, _readers[i].Position);
            int count = (int)(_through - first);
            if (target.Values.Length < count) target.Values = new T[count];
            _values.AsSpan(_start + (int)(first - _first), count).CopyTo(target.Values);
            target.Count = count;
            target.Through = _through;
            if (target.Readers.Length < _readerCount) target.Readers = new EventReaderPosition[_readerCount];
            for (int i = 0; i < _readerCount; i++)
                target.Readers[i] = new(_readers[i].Id, _readers[i].Position);
            target.ReaderCount = _readerCount;
            return target;
        }
    }

    public void RestoreData(EventLogData<T> data) => RestoreData(data, default);

    internal void Clear(JobHandle access)
    {
        using SynchronousResourceAccess acquired = EnterAccess(JobAccessMode.Write, 0, 0, whole: true, access);
        lock (_gate)
        {
            CheckActive();
            _start = 0;
            _first = _reservedThrough = _allocatedThrough = _through;
            for (int i = 0; i < _readerCount; i++)
            {
                _readers[i].Position = _through;
                _readers[i].LastAdvance = _stepCount;
                _readers[i].Reader.ClearRead();
            }
        }
    }

    internal void RestoreData(EventLogData<T> data, JobHandle access, bool restoreAnonymousReaders = false)
    {
        using SynchronousResourceAccess acquired = EnterAccess(JobAccessMode.Write, 0, 0, whole: true, access);
        lock (_gate)
        {
            CheckActive();
            if (_values.Length < data.Count) _values = new T[data.Count];
            data.Values.AsSpan(0, data.Count).CopyTo(_values);
            _start = 0;
            _first = data.Through - (ulong)data.Count;
            _through = _reservedThrough = _allocatedThrough = data.Through;
            _ready = _publishReady = _spaceReady = default;
            _stepCount = 0;
            for (int i = 0; i < _readerCount; i++)
            {
                _readers[i].Position = _through;
                _readers[i].LastAdvance = 0;
                _readers[i].Reader.ClearRead();
            }
            int anonymous = 0;
            for (int i = 0; i < data.ReaderCount; i++)
            {
                EventReaderPosition saved = data.Readers[i];
                EventReader<T>? reader;
                if (saved.Id != Guid.Empty) reader = Watch(saved.Id);
                else
                {
                    while (anonymous < _readerCount && _readers[anonymous].Id != Guid.Empty) anonymous++;
                    if (anonymous >= _readerCount && restoreAnonymousReaders) AddReader(Guid.Empty, _through);
                    reader = anonymous < _readerCount ? _readers[anonymous++].Reader : null;
                }
                if (reader is not null) _readers[reader.Index].Position = saved.Position;
            }
        }
    }
}
