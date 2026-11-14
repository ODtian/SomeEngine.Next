using System.Numerics;
using System.Runtime.CompilerServices;
using SomeEngine.Core.Diagnostics;

namespace SomeEngine.Job;

/// <summary>A producer's ordered events, retained until every registered reader has advanced.</summary>
public sealed partial class EventLog<T> : IDisposable where T : unmanaged
{
    private readonly Lock _gate = new();
    private Scheduler? _scheduler;
    private JobResourceToken _storage, _changes;
    private T[] _values;
    private int _start, _openReads;
    private ulong _first, _through, _reservedThrough, _allocatedThrough, _stepCount;
    private JobHandle _ready, _spaceReady, _publishReady;
    private bool _disposed;
    private readonly int _stalledStepLimit;
    private ReaderEntry[] _readers = [];
    private int _readerCount;
    private Dictionary<Guid, EventReader<T>>? _named;

    private struct ReaderEntry
    {
        internal Guid Id;
        internal ulong Position, LastAdvance;
        internal EventReader<T> Reader;
    }

    public EventLog(Scheduler? scheduler = null, int initialCapacity = 0, int stalledStepLimit = 120)
    {
        _values = initialCapacity == 0 ? [] : new T[initialCapacity];
        _stalledStepLimit = stalledStepLimit;
        if (scheduler is not null) BindScheduler(scheduler);
    }

    public bool HasReaders { get { lock (_gate) return _readerCount != 0; } }
    public ulong Through { get { lock (_gate) return _through; } }
    public long StorageBytes { get { lock (_gate) return _values.LongLength * Unsafe.SizeOf<T>(); } }
    public JobResourceAccess Read { get { GetScheduler(); return JobResourceAccess.Read(_storage); } }
    public JobResourceAccess Write { get { GetScheduler(); return JobResourceAccess.Write(_storage); } }
    private readonly object _storageKey = new();
    private readonly object _changesKey = new();

    internal void BindScheduler(Scheduler scheduler)
    {
        lock (_gate)
        {
            DevelopmentCheck.Require(_scheduler is null || ReferenceEquals(_scheduler, scheduler),
                "An event log uses one scheduler.");
            _scheduler = scheduler;
            _storage = scheduler.GetResourceToken(_storageKey);
            _changes = scheduler.GetResourceToken(_changesKey);
            for (int i = 0; i < _readerCount; i++) _readers[i].Reader.ClearRead();
        }
    }

    private Scheduler GetScheduler()
    {
        lock (_gate)
        {
            CheckActive();
            if (_scheduler is null) BindScheduler(Scheduler.Executing ?? JobSystem.Current);
            return _scheduler!;
        }
    }

    private void CheckActive() => DevelopmentCheck.Require(!_disposed, "The event log has been disposed.");

    private SynchronousResourceAccess EnterAccess(JobAccessMode mode, ulong first, ulong through,
        bool whole, JobHandle access)
    {
        Scheduler? scheduler = _scheduler ?? Scheduler.Executing;
        if (scheduler is null) return default;
        if (_scheduler is null) BindScheduler(scheduler);
        JobResourceAccess required = mode == JobAccessMode.Read
            ? JobResourceAccess.Read(_storage) : JobResourceAccess.Write(_storage);
        if (!whole) required = required.WithRange((long)first, (long)(through - first));
        if (access.Index != 0) return default;
        if (scheduler.GetCurrentScope().Index == 0) return scheduler.AcquireSynchronousAccess([required]);
        DevelopmentCheck.Require(scheduler.HasCurrentAccess(required), "The current Job requires the event access.");
        return default;
    }

    public EventWriter<T> Reserve(int count) => Reserve(count, default);

    internal EventWriter<T> Reserve(int count, JobHandle access)
    {
        SynchronousResourceAccess acquired = EnterAccess(JobAccessMode.Write, 0, 0, whole: true, access);
        try
        {
            lock (_gate)
            {
                CheckActive();
                ulong first = _reservedThrough;
                ulong through = first + (ulong)(_readerCount == 0 ? 0 : count);
                _reservedThrough = through;
                EnsureSpace(through);
                EventWriteRange range = CreateWriteRange(first, through, default);
                return new EventWriter<T>(_values.AsSpan(_start + (int)(first - _first), (int)(through - first)), range, acquired);
            }
        }
        catch { acquired.Dispose(); throw; }
    }

    /// <summary>Assigns an interval and schedules any storage maintenance before writers start.</summary>
    public EventWriteRange ReserveRange(int count, JobHandle dependency = default)
    {
        Scheduler scheduler = GetScheduler();
        lock (_gate)
        {
            CheckActive();
            ulong first = _reservedThrough;
            ulong through = first + (ulong)(_readerCount == 0 ? 0 : count);
            _reservedThrough = through;
            JobHandle preceding = scheduler.CombineDependencies([_spaceReady, dependency]);
            _spaceReady = scheduler.ScheduleStages(new EnsureWriteSpace(this, through), preceding);
            return CreateWriteRange(first, through, _spaceReady);
        }
    }

    private EventWriteRange CreateWriteRange(ulong first, ulong through, JobHandle ready) =>
        new(first, through, ready, _scheduler is null ? default
            : JobResourceAccess.Write(_storage).WithRange((long)first, (long)(through - first)));

    public EventWriter<T> Open(scoped in EventWriteRange range)
    {
        SynchronousResourceAccess acquired = EnterAccess(JobAccessMode.Write, range.First, range.Through, whole: false, default);
        try
        {
            lock (_gate)
            {
                CheckActive();
                DevelopmentCheck.Require(range.Through <= _allocatedThrough && range.First >= _first,
                    "The event write interval requires completed storage preparation.");
                if (_scheduler is not null)
                    DevelopmentCheck.Require(range.Write.CoversIdentityAndMode(JobResourceAccess.Write(_storage)),
                        "The event write interval belongs to this log.");
                return new EventWriter<T>(_values.AsSpan(_start + (int)(range.First - _first),
                    (int)(range.Through - range.First)), range, acquired);
            }
        }
        catch { acquired.Dispose(); throw; }
    }

    public JobHandle Publish(in EventWriteRange range, JobHandle written = default)
    {
        Scheduler? scheduler = _scheduler;
        if (written.Scheduler is null && (scheduler is null || scheduler.GetCurrentScope().Index == 0))
        {
            Commit(range.Through, default);
            return default;
        }
        scheduler ??= GetScheduler();
        lock (_gate)
        {
            JobHandle dependency = scheduler.CombineDependencies([_publishReady, written, range.Ready]);
            _publishReady = scheduler.Schedule(new PublishEvents(this, range.Through), dependency: dependency);
            return _publishReady;
        }
    }

    /// <summary>
    /// Marks the end of one producer step. A reader that still has published events to read and has not
    /// advanced for the configured number of steps is reported in Debug builds.
    /// </summary>
    public void EndStep()
    {
        lock (_gate)
        {
            CheckActive();
            ulong step = ++_stepCount;
            for (int i = 0; i < _readerCount; i++)
            {
                ref ReaderEntry reader = ref _readers[i];
                if (reader.Position >= _through) reader.LastAdvance = step;
                DevelopmentCheck.Require(_stalledStepLimit <= 0 || step - reader.LastAdvance < (ulong)_stalledStepLimit,
                    "An event reader has not advanced for the configured number of steps while the log retains its events.");
            }
        }
    }

    private void Commit(ulong through, JobHandle ready)
    {
        lock (_gate)
        {
            CheckActive();
            DevelopmentCheck.Require(through >= _through && through <= _allocatedThrough,
                "Publishing requires an ordered interval of written events.");
            _through = through;
            _ready = ready;
        }
    }

    private readonly struct PublishEvents(EventLog<T> log, ulong through) : IJob
    {
        public void Execute() => log.Commit(through, log._scheduler!.GetCurrentScope());
    }

    private struct EnsureWriteSpace(EventLog<T> log, ulong through) : IStagedJob
    {
        public static int StageCount => 2;
        public void Prepare(int stage, ref AccessBuilder<JobResourceAccess> accesses)
        {
            if (stage == 0) accesses.Add(JobResourceAccess.Write(log._changes));
            else lock (log._gate)
            {
                if (log._start + (long)(through - log._first) > log._values.LongLength)
                    accesses.Add(JobResourceAccess.Write(log._storage));
            }
        }
        public void Execute() { lock (log._gate) { log.CheckActive(); log.EnsureSpace(through); } }
        public void Release() { }
    }

    private void EnsureSpace(ulong through)
    {
        ulong first = _through;
        for (int i = 0; i < _readerCount; i++) first = Math.Min(first, _readers[i].Position);
        _start += (int)(first - _first);
        _first = first;
        int needed = (int)(through - first);
        if (_start + needed > _values.Length)
        {
            int kept = (int)(_allocatedThrough - first);
            if (needed <= _values.Length && _openReads == 0)
                _values.AsSpan(_start, kept).CopyTo(_values);
            else
            {
                int capacity = needed <= _values.Length ? _values.Length
                    : (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(16, needed));
                T[] values = GC.AllocateUninitializedArray<T>(capacity);
                _values.AsSpan(_start, kept).CopyTo(values);
                _values = values;
            }
            _start = 0;
        }
        _allocatedThrough = Math.Max(_allocatedThrough, through);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _spaceReady.Complete();
        _publishReady.Complete();
        SynchronousResourceAccess access = EnterAccess(JobAccessMode.Write, 0, 0, whole: true, default);
        try { ClearData(); }
        finally { access.Dispose(); }
        ReleaseTokens();
    }

    internal void Dispose(JobHandle access)
    {
        if (access.Index == 0) { Dispose(); return; }
        if (_disposed) return;
        ClearData();
        _scheduler!.ScheduleFinally(new ReleaseEvents(this), dependency: access, options: JobScheduleOptions.Default);
    }

    private void ClearData()
    {
        lock (_gate)
        {
            _disposed = true;
            for (int i = 0; i < _readerCount; i++)
            {
                _readers[i].Reader.Disposed = true;
                _readers[i].Reader.ClearRead();
            }
            _readers = [];
            _named?.Clear();
            _values = [];
            _readerCount = _start = 0;
        }
    }

    private void ReleaseTokens()
    {
        if (_scheduler is null) return;
        _scheduler.ReleaseResourceToken(_storage);
        _scheduler.ReleaseResourceToken(_changes);
    }

    private readonly struct ReleaseEvents(EventLog<T> log) : IJob
    { public void Execute() => log.ReleaseTokens(); }
}
