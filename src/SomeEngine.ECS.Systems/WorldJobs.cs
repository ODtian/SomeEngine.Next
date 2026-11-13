using System.Runtime.CompilerServices;
using SomeEngine.ECS.Storage;
using SomeEngine.Job;

namespace SomeEngine.ECS.Systems;

public sealed partial class WorldJobs(Scheduler scheduler) : WorldExecution
{
    [ThreadStatic] private static AccessFrame? s_current;
    [ThreadStatic] private static AccessFrame[]? s_free;
    [ThreadStatic] private static int s_freeCount;

    private readonly Scheduler _scheduler = scheduler;
    private readonly Lock _resourcesLock = new();
    private readonly object _usesLock = new();
    private readonly WorldResourceEntry?[][] _resources = [[], [], [], [], []];
    private World _world = null!;
    private JobResource _structure;
    private int _uses, _disposingThread;
    private readonly JobLifetime _jobLifetime = new("World");
    private bool IsClosed => !_jobLifetime.IsOpen;
    private bool _clearing;

    public override Scheduler Scheduler => _scheduler;
    internal JobResource Structure => _structure;
    internal override bool CanBorrowStructureRead
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => s_current is { StructureWrite: false } frame
            && ReferenceEquals(frame.Jobs, this) && frame.JobDepth == JobThreads.Depth;
    }

    internal override void Attach(World world)
    {
        _world = world;
        _structure = _scheduler.CreateResource("World structure", _jobLifetime);
        world.BindEventScheduler(_scheduler);
    }

    internal override void CheckActive()
    {
        if (!IsClosed) return;
        // Only the disposal call itself is exempt; jobs that this thread helps run while waiting are not.
        if (Environment.CurrentManagedThreadId == _disposingThread && !JobThreads.IsActive) return;
        if (s_current is { } frame && ReferenceEquals(frame.Jobs, this) && frame.JobDepth == JobThreads.Depth) return;
        if (JobThreads.IsActive && _jobLifetime.OwnsCurrentScope()) return;
        throw new ObjectDisposedException(nameof(World));
    }

    internal JobResource Resource(WorldValueKind kind, int type)
    {
        if (FindResourceEntry(kind, type) is { } found) return found.Value;
        return CreateValueResource(kind, type);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private WorldResourceEntry? FindResourceEntry(WorldValueKind kind, int type)
    {
        WorldResourceEntry?[] entries = Volatile.Read(ref _resources[(int)kind]);
        return (uint)type < (uint)entries.Length ? Volatile.Read(ref entries[type]) : null;
    }

    private JobResource CreateValueResource(WorldValueKind kind, int type)
    {
        lock (_resourcesLock)
        {
            WorldResourceEntry?[] entries = _resources[(int)kind];
            if ((uint)type < (uint)entries.Length && entries[type] is { } found) return found.Value;
            bool grew = type >= entries.Length;
            if (grew) Array.Resize(ref entries, Math.Max(type + 1, Math.Max(16, entries.Length * 2)));
            JobResource resource = _scheduler.CreateResource($"World {kind} {type}");
            Volatile.Write(ref entries[type], new WorldResourceEntry(resource));
            if (grew) Volatile.Write(ref _resources[(int)kind], entries);
            return resource;
        }
    }

    private sealed class WorldResourceEntry(JobResource value)
    {
        internal readonly JobResource Value = value;
        internal QueryValues? Queries;
    }

    private sealed class QueryValues(QueryDefinition definition, in AccessBuilder<JobResourceAccess> values, QueryValues? next)
    {
        internal readonly QueryDefinition Definition = definition;
        internal readonly AccessBuilder<JobResourceAccess> Values = values;
        internal readonly QueryValues? Next = next;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void BeginUse()
    {
        if (!JobThreads.IsActive && !IsClosed && !Volatile.Read(ref _clearing))
        {
            Interlocked.Increment(ref _uses);
            if (!IsClosed && !Volatile.Read(ref _clearing)) return;
            EndUse();
        }
        BeginUseSlow();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void BeginUseSlow()
    {
        bool child = JobThreads.IsActive && _jobLifetime.OwnsCurrentScope();
        lock (_usesLock)
        {
            while (_clearing && !child) Monitor.Wait(_usesLock);
            ObjectDisposedException.ThrowIf(IsClosed && !child, _world);
            Interlocked.Increment(ref _uses);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void EndUse()
    {
        if (Interlocked.Decrement(ref _uses) != 0) return;
        if (!IsClosed && !Volatile.Read(ref _clearing)) return;
        NotifyUses();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void NotifyUses()
    {
        lock (_usesLock)
        {
            if (Volatile.Read(ref _uses) == 0 && (IsClosed || _clearing)) Monitor.PulseAll(_usesLock);
        }
    }

    internal SynchronousResourceAccess BeginQueryAccess()
    {
        BeginUse();
        try { return _scheduler.AcquireSynchronousAccess([JobResourceAccess.Read(_structure)]); }
        catch { EndUse(); throw; }
    }

    internal void EndQueryAccess(SynchronousResourceAccess access)
    {
        try { access.Dispose(); }
        finally { EndUse(); }
    }

    internal override WorldAccess OpenStructure(bool write)
    {
        if (HasStructureWrite()) return default;
        EnterStructure(write);
        return new(this);
    }

    internal override WorldAccess OpenValues(ReadOnlySpan<WorldValueAccess> values)
        => TryOpenValues(values) ? new(this) : default;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ValuesScope OpenTypedValues(ReadOnlySpan<WorldValueAccess> values)
        => TryOpenValues(values) ? new(this) : default;

    private bool TryOpenValues(ReadOnlySpan<WorldValueAccess> values)
    {
        if (HasStructureWrite()) return false;
        EnterValues(values);
        return true;
    }

    internal readonly struct ValuesScope(WorldJobs? owner) : IDisposable
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Dispose() => owner?.ExitCore();
    }

    internal override void EnterStructure(bool write)
    {
        if (s_current is { } current && ReferenceEquals(current.Jobs, this) && current.JobDepth == JobThreads.Depth)
        {
            if (write && (!current.StructureWrite || current.JobDepth != JobThreads.Depth))
                Require(JobResourceAccess.Write(_structure), true);
            current.Depth++;
            return;
        }
        Enter(write, default(AccessBuilder<JobResourceAccess>));
    }

    internal override void EnterValues(ReadOnlySpan<WorldValueAccess> values)
    {
        foreach (WorldValueAccess value in values) RequireSharedJobValue(value);
        if (HasStructureWrite()) { EnterStructure(false); return; }
        foreach (WorldValueAccess value in values)
            if (value.Structural && value.Write) { EnterStructure(true); return; }
        if (HasRangeWrite)
        {
            Enter(false, default(AccessBuilder<JobResourceAccess>));
            try { foreach (WorldValueAccess value in values) RequireValue(value); }
            catch { Exit(); throw; }
            return;
        }
        AccessBuilder<JobResourceAccess> accesses = new();
        try
        {
            foreach (WorldValueAccess value in values) AddValue(ref accesses, value);
            Enter(false, accesses);
        }
        finally { accesses.Clear(); }
    }

    private void AddValue(ref AccessBuilder<JobResourceAccess> accesses, in WorldValueAccess value)
    {
        if (value.Structural) return;
        JobResource resource = Resource(value.Kind, value.Type);
        accesses.Add(value.Write ? JobResourceAccess.Write(resource) : JobResourceAccess.Read(resource));
        if (value.Tracked && value.Kind is WorldValueKind.Table or WorldValueKind.Sparse)
            accesses.Add(value.Write ? JobResourceAccess.Write(Resource(ChangeKind(value.Kind), value.Type))
                : JobResourceAccess.Read(Resource(ChangeKind(value.Kind), value.Type)));
    }

    internal override void EnterQuery(Query query, ReadOnlySpan<int> writes, bool writeAll)
    {
        if (HasStructureWrite()) { EnterStructure(false); return; }
        QueryDescription description = query.Description;
        QueryDefinition definition = description.Definition;
        int writeType = !writeAll && writes.Length == 1 && definition.AccessTypes.Length <= 2
            && description.Shared.Length == 0 && description.Groups.Length == 0 ? writes[0] : -1;
        if (writeType >= 0 && FindQueryValues(definition, writeType) is { } cached)
        {
            Enter(false, in cached.Values);
            return;
        }
        EnterQueryCold(description, definition, writes, writeAll, writeType);
    }

    private QueryValues? FindQueryValues(QueryDefinition definition, int writeType)
    {
        WorldResourceEntry? entry = FindResourceEntry(WorldValueKind.Table, writeType);
        if (entry is null) return null;
        for (QueryValues? values = Volatile.Read(ref entry.Queries); values is not null; values = values.Next)
            if (ReferenceEquals(values.Definition, definition)) return values;
        return null;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void EnterQueryCold(QueryDescription description, QueryDefinition definition, ReadOnlySpan<int> writes, bool writeAll, int writeType)
    {
        AccessBuilder<JobResourceAccess> accesses = new();
        try
        {
            BuildQueryValues(description, definition, writes, writeAll, ref accesses);
            bool cache = writeType >= 0 && definition.AccessTypes.AsSpan().Contains(writeType)
                && !ComponentTypes.Get(writeType).UsesStructureWrites;
            Enter(false, in accesses);
            try { if (cache) CacheQueryValues(definition, writeType, in accesses); }
            catch { Exit(); throw; }
        }
        finally { accesses.Clear(); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void BuildQueryValues(QueryDescription description, QueryDefinition definition, ReadOnlySpan<int> writes, bool writeAll,
        ref AccessBuilder<JobResourceAccess> accesses)
    {
        foreach (int type in definition.AccessTypes)
        {
            ComponentType component = ComponentTypes.Get(type);
            bool structural = component.UsesStructureWrites;
            bool write = !structural && (writeAll || writes.Contains(type));
            AddValue(ref accesses, new(type, WorldValueKind.Table, write, component.TracksWrites, structural));
        }
    }

    private void CacheQueryValues(QueryDefinition definition, int writeType, in AccessBuilder<JobResourceAccess> accesses)
    {
        WorldResourceEntry entry = FindResourceEntry(WorldValueKind.Table, writeType)!;
        lock (_resourcesLock)
        {
            QueryValues? first = Volatile.Read(ref entry.Queries);
            for (QueryValues? values = first; values is not null; values = values.Next)
                if (ReferenceEquals(values.Definition, definition)) return;
            var cached = new QueryValues(definition, in accesses, first);
            Volatile.Write(ref entry.Queries, cached);
        }
    }

    private void Enter(bool structureWrite, in AccessBuilder<JobResourceAccess> accesses)
    {
        if (s_current is { } current && ReferenceEquals(current.Jobs, this) && current.JobDepth == JobThreads.Depth)
        {
            if (!current.StructureWrite)
            {
                if (accesses.Count != 0 && !current.ValuesBound && current.Access.Handle.Index != 0 && !JobThreads.IsActive)
                {
                    _scheduler.AppendSynchronousAccess(current.Handle, accesses);
                    current.ValuesBound = true;
                }
                else for (int i = 0; i < accesses.Count; i++) Require(accesses.Get(i), accesses.Get(i).Mode != JobAccessMode.Read);
            }
            current.Depth++;
            return;
        }

        bool job = JobThreads.IsActive;
        bool disposal = Environment.CurrentManagedThreadId == _disposingThread;
        SynchronousResourceAccess access = default;
        bool counted = !job && !disposal;
        if (counted) BeginUse();
        try
        {
            if (job)
            {
                JobResourceAccess structure = structureWrite ? JobResourceAccess.Write(_structure) : JobResourceAccess.Read(_structure);
                _scheduler.RequireCurrentAccess(structure, structureWrite);
                bool completeStructure = _scheduler.HasCurrentAccess(JobResourceAccess.Write(_structure));
                if (completeStructure)
                {
                    _scheduler.RequireCurrentAccess(JobResourceAccess.Write(_structure), requireSingleWorkItem: true);
                    structureWrite = true;
                }
                else for (int i = 0; i < accesses.Count; i++)
                    _scheduler.RequireCurrentAccess(accesses.Get(i), accesses.Get(i).Mode != JobAccessMode.Read);
            }
            else
            {
                if (structureWrite)
                {
                    AccessBuilder<JobResourceAccess> structure = new();
                    try
                    {
                        _world.CollectEventAccesses(ref structure, true);
                        access = _scheduler.AcquireSynchronousAccess(JobResourceAccess.Write(_structure), structure);
                    }
                    finally { structure.Clear(); }
                }
                else access = _scheduler.AcquireSynchronousAccess(JobResourceAccess.Read(_structure), accesses);
            }
            if (counted) CheckActive();
            AccessFrame frame = RentFrame();
            frame.Jobs = this;
            frame.Previous = s_current;
            frame.Access = access;
            frame.Handle = job ? _scheduler.GetCurrentScope() : access.Handle;
            frame.StructureWrite = structureWrite;
            frame.JobDepth = JobThreads.Depth;
            frame.Counted = counted;
            frame.ValuesBound = job || accesses.Count != 0;
            frame.Depth = 1;
            s_current = frame;
        }
        catch
        {
            access.Dispose();
            if (counted) EndUse();
            throw;
        }
    }

    internal override void RequireValue(in WorldValueAccess value)
    {
        RequireSharedJobValue(value);
        if (HasStructureWrite()) return;
        if (HasRangeWrite && value.Kind == WorldValueKind.Table && value.Write && !value.Structural)
        {
            RequireRangeValue(value);
            return;
        }
        if (value.Structural)
        {
            Require(value.Write ? JobResourceAccess.Write(_structure) : JobResourceAccess.Read(_structure), value.Write);
            return;
        }
        JobResource resource = Resource(value.Kind, value.Type);
        Require(value.Write ? JobResourceAccess.Write(resource) : JobResourceAccess.Read(resource), value.Write);
        if (value.Write && value.Tracked)
            Require(JobResourceAccess.Write(Resource(ChangeKind(value.Kind), value.Type)), true);
    }

    private void RequireSharedJobValue(in WorldValueAccess value)
    {
        if (!value.Structural || value.Kind != WorldValueKind.Table || !JobThreads.IsActive) return;
        if (s_current is { Counted: true } current && ReferenceEquals(current.Jobs, this)
            && current.JobDepth == JobThreads.Depth) return;
        ComponentType component = ComponentTypes.Get(value.Type);
        if (component.IsShared) RequireJobStorage(component);
    }

    private bool HasStructureWrite()
    {
        if (s_current is { } frame && ReferenceEquals(frame.Jobs, this) && frame.JobDepth == JobThreads.Depth)
            return frame.StructureWrite;
        if (JobThreads.IsActive && _scheduler.HasCurrentAccess(JobResourceAccess.Write(_structure)))
        {
            _scheduler.RequireCurrentAccess(JobResourceAccess.Write(_structure), true);
            return true;
        }
        return false;
    }

    private void Require(JobResourceAccess required, bool single)
    {
        if (JobThreads.IsActive) _scheduler.RequireCurrentAccess(required, single);
        else if (s_current is { } current && ReferenceEquals(current.Jobs, this))
            _scheduler.RequireAccess(current.Handle, required, single);
        else throw new InvalidOperationException("Open a World value scope before borrowing a connected value.");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal override void Exit() => ExitCore();

    private void ExitCore()
    {
        AccessFrame frame = s_current!;
        if (--frame.Depth != 0) return;
        s_current = frame.Previous;
        bool counted = frame.Counted;
        try { frame.Access.Dispose(); }
        finally
        {
            frame.Jobs = null!;
            frame.Previous = null;
            frame.Access = default;
            frame.Handle = default;
            ReturnFrame(frame);
            if (counted) EndUse();
        }
    }

    internal override void ValidateDisposePreconditions()
    {
        if (JobThreads.IsActive || s_current is { } current && ReferenceEquals(current.Jobs, this))
            throw new InvalidOperationException("Finish the current World scope before disposing it.");
    }

    internal override void BeginDispose()
    {
        ValidateDisposePreconditions();
        lock (_usesLock)
        {
            while (_clearing) Monitor.Wait(_usesLock);
            _disposingThread = Environment.CurrentManagedThreadId;
        }
        _jobLifetime.CloseAndWait();
        WaitForUses();
    }

    internal override void BeginClear()
    {
        if (JobThreads.IsActive || s_current is { } current && ReferenceEquals(current.Jobs, this))
            throw new InvalidOperationException("Finish the current World scope before clearing it.");
        lock (_usesLock)
        {
            while (_clearing) Monitor.Wait(_usesLock);
            ObjectDisposedException.ThrowIf(IsClosed, _world);
            Volatile.Write(ref _clearing, true);
            _disposingThread = Environment.CurrentManagedThreadId;
        }
        WaitForUses();
    }

    internal override void EndClear()
    {
        lock (_usesLock)
        {
            Volatile.Write(ref _clearing, false);
            _disposingThread = 0;
            Monitor.PulseAll(_usesLock);
        }
    }

    private void WaitForUses()
    {
        while (true)
        {
            lock (_usesLock) if (Interlocked.CompareExchange(ref _uses, 0, 0) == 0) return;
            if (!_scheduler.HelpComplete())
                lock (_usesLock) if (Interlocked.CompareExchange(ref _uses, 0, 0) != 0) Monitor.Wait(_usesLock, 10);
        }
    }

    internal override void Release()
    {
        for (int kind = 0; kind < _resources.Length; kind++)
        {
            foreach (WorldResourceEntry? entry in _resources[kind])
                if (entry is not null) _scheduler.ReleaseResource(entry.Value);
            _resources[kind] = [];
        }
        _scheduler.ReleaseResource(_structure);
        _disposingThread = 0;
    }

    private sealed class AccessFrame
    {
        internal WorldJobs Jobs = null!;
        internal AccessFrame? Previous;
        internal SynchronousResourceAccess Access;
        internal JobHandle Handle;
        internal int Depth;
        internal int JobDepth;
        internal bool StructureWrite, Counted, ValuesBound;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static AccessFrame RentFrame()
    {
        if (s_freeCount == 0) return CreateFrame();
        int index = --s_freeCount;
        AccessFrame frame = s_free![index];
        s_free[index] = null!;
        return frame;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static AccessFrame CreateFrame() => new();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ReturnFrame(AccessFrame frame)
    {
        AccessFrame[]? free = s_free;
        if (free is null || s_freeCount == free.Length) free = GrowFramePool(free);
        free[s_freeCount++] = frame;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static AccessFrame[] GrowFramePool(AccessFrame[]? free)
    {
        if (free is null) return s_free = new AccessFrame[4];
        Array.Resize(ref free, free.Length * 2);
        return s_free = free;
    }
}
