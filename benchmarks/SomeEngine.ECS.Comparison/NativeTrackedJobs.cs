using SomeEngine.ECS.Entities;
using SomeEngine.Job;
using System.Runtime.CompilerServices;

namespace SomeEngine.ECS.Comparison;

internal interface INativeTrackedRange
{
    void Execute(NativeTrackedContext context, ulong version, int chunk, int start);
}
internal interface INativeTrackedQuery<T> : IDisposable where T : struct, INativeTrackedRange
{
    void Build(NativeQueryRanges<T> ranges, NativeTrackedContext context);
    void Execute(NativeTrackedContext context, ulong version);
}
internal interface INativeTrackedReads
{
    bool IsAlive(Entity entity);
    Entity ReadParent(Entity entity);
}
internal readonly struct ArchiveIdentityCheck(NativeArchiveStore store)
{
    private readonly int[] _generations = store.Generations;
    private readonly bool[] _alive = store.AliveIds;
    private readonly int _used = store.Used;
    internal bool IsAlive(Entity entity) => (uint)(entity.Id - 1) < (uint)_used
        && _alive[entity.Id] && _generations[entity.Id] == entity.Generation;
}
internal sealed class NativeTrackedSummary(int count)
{
    internal readonly ulong[] Words = new ulong[(count + 63) >> 6];
    internal ulong Latest;
}

internal sealed class NativeTrackedContext(FullArchiveWorld world, NativeArchiveStore store, Scheduler scheduler,
    ReferenceSparseStores<Entity, NativeArchiveValues> sparse, ReferenceResources resources, Entity[] entities, Entity secondRoot,
    Func<ulong> advance)
{
    internal readonly FullArchiveWorld World = world;
    internal readonly NativeArchiveStore Store = store;
    internal readonly Scheduler Scheduler = scheduler;
    internal readonly Entity[] Entities = entities;
    internal readonly ReferenceSparseStore<SaveSparseRow, Entity, NativeArchiveValues> Sparse = sparse.Store<SaveSparseRow>();
    internal readonly JobResource Structure = scheduler.CreateResource("Tracked World structure");
    private readonly JobResource _counter = scheduler.CreateResource("Tracked World Counter");
    private readonly JobResource _position = scheduler.CreateResource("Tracked World Position");
    private readonly JobResource _changes = scheduler.CreateResource("Tracked World Counter changes");
    private readonly JobResource _sparse = scheduler.CreateResource("Tracked World Sparse");
    private readonly JobResource _resource = scheduler.CreateResource("Tracked World Resource");
    private NativeTrackedSummary?[] _summaries = new NativeTrackedSummary[16];
    private int _chunks;
    private JobHandle _access;
    internal void BeginQuery() => _chunks = 0;
    internal void BindChunk(int chunk, int count)
    {
        if (chunk >= _summaries.Length) Array.Resize(ref _summaries, Math.Max(chunk + 1, _summaries.Length * 2));
        if (_summaries[chunk] is not { } summary || summary.Words.Length != ((count + 63) >> 6))
            _summaries[chunk] = new(count);
        _chunks = Math.Max(_chunks, chunk);
    }
    internal ulong BeginValues(JobHandle access) { _access = access; return advance(); }
    internal void Accesses(ref AccessBuilder<JobResourceAccess> accesses, bool wholeChanges = false)
    {
        accesses.Add(JobResourceAccess.Write(_counter)); accesses.Add(JobResourceAccess.Read(_position));
        if (wholeChanges) accesses.Add(JobResourceAccess.Write(_changes));
        else for (int chunk = 1; chunk <= _chunks; chunk++) accesses.Add(JobResourceAccess.Write(_changes, chunk, 1));
        accesses.Add(JobResourceAccess.Read(_sparse)); accesses.Add(JobResourceAccess.Read(_resource));
    }
    internal int ReadResource()
    {
        Scheduler.RequireAccess(_access, JobResourceAccess.Read(Structure), false);
        Scheduler.RequireAccess(_access, JobResourceAccess.Read(_sparse), false);
        Scheduler.RequireAccess(_access, JobResourceAccess.Read(_resource), false);
        return (int)resources.Read<ResourceData<ArchiveResources.Kind0>>().Value;
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int ReadExtra<TRead>(int ordinal, ref TRead read) where TRead : struct, INativeTrackedReads, allows ref struct
    {
        Entity target = Entities[TrackedArchiveJobs.Pick(ordinal, Entities.Length)];
        int extra = read.ReadParent(target) == secondRoot ? 1 : 0;
        if (read.IsAlive(target) && Sparse.Has(target.Id))
        {
            if (!read.IsAlive(target)) throw new ArgumentException("Entity generation has expired.");
            extra += (int)(Sparse.Read(target.Id).Value.Value & 7);
        }
        return extra;
    }
    internal void StampWords(int chunk, int start, int count, ulong version)
        => _summaries[chunk]!.Words.AsSpan(start >> 6, ((start + count + 63) >> 6) - (start >> 6)).Fill(version);
    internal void Apply<TRead>(Span<SaveCounter> counters, ReadOnlySpan<Position> positions, Span<SaveNativeVersions> versions,
        TRead read, ulong version, int chunk, int start) where TRead : struct, INativeTrackedReads, allows ref struct
    {
        int value = ReadResource();
        for (int i = 0; i < counters.Length; i++)
        {
            counters[i].Value += (int)positions[i].X + value + ReadExtra(counters[i].Ordinal, ref read);
            versions[i].Written = version;
        }
        StampWords(chunk, start, counters.Length, version);
    }
    internal void Merge()
    {
        for (int chunk = 1; chunk <= _chunks; chunk++)
        {
            var summary = _summaries[chunk]!; ulong latest = 0;
            foreach (ulong version in summary.Words) latest = Math.Max(latest, version);
            summary.Latest = latest;
        }
    }
    internal void CheckSummaries(ulong version)
    {
        for (int chunk = 1; chunk <= _chunks; chunk++)
            if (_summaries[chunk]!.Latest != version) throw new InvalidOperationException("Native tracked completion summary differs.");
    }
    internal void Dispose()
    {
        Scheduler.ReleaseResource(_counter); Scheduler.ReleaseResource(_position); Scheduler.ReleaseResource(_changes);
        Scheduler.ReleaseResource(_sparse); Scheduler.ReleaseResource(_resource); Scheduler.ReleaseResource(Structure);
        _summaries = [];
    }
}

internal sealed class NativeTrackedArchiveJobs<T, TQuery> : TrackedArchiveJobs
    where T : struct, INativeTrackedRange where TQuery : struct, INativeTrackedQuery<T>
{
    private readonly Scheduler _scheduler;
    private TQuery _query;
    private readonly NativeTrackedContext _context;
    private JobHandle _last;
    internal NativeTrackedArchiveJobs(Scheduler scheduler, TQuery query, NativeTrackedContext context) : base(context.World, context.Entities)
    { _scheduler = scheduler; _query = query; _context = context; }
    internal override void Run(string mode)
    {
        bool dependent = mode == "dependent"; BeginRun(dependent);
        if (mode is "execute" or "parallel")
        {
            using var access = _scheduler.AcquireSynchronousAccess([JobResourceAccess.Read(_context.Structure)]);
            var ranges = NativeQueryRanges<T>.Rent(); AccessBuilder<JobResourceAccess> values = new();
            try
            {
                _context.BeginQuery();
                if (mode != "execute") _query.Build(ranges, _context);
                _context.Accesses(ref values, mode == "execute");
                _scheduler.AppendSynchronousAccess(access.Handle, values);
                ulong version = _context.BeginValues(access.Handle);
                if (mode == "execute") _query.Execute(_context, version);
                else Execute(ranges, version, access.Handle);
                _context.Merge();
            }
            finally { values.Clear(); ranges.Return(); }
        }
        else
        {
            _last = _scheduler.ScheduleStages(new Stages(this));
            if (dependent) _last = _scheduler.ScheduleStages(new Stages(this), _last);
            _last.Complete();
        }
        EndRun();
    }
    private void Execute(NativeQueryRanges<T> ranges, ulong version, JobHandle access)
        => _scheduler.ExecuteRanges(ranges.Items, ranges.Count, new Work(_context, version), access);
    private readonly struct Work(NativeTrackedContext context, ulong version) : IRangeWork<NativeQueryRange<T>>
    { public void Execute(in NativeQueryRange<T> range) => range.Values.Execute(context, version, range.Chunk, range.Start); }
    private struct Stages(NativeTrackedArchiveJobs<T, TQuery> jobs) : IStagedJob
    {
        private NativeQueryRanges<T>? _ranges;
        public static int StageCount => 2;
        public void Prepare(int stage, ref AccessBuilder<JobResourceAccess> accesses)
        {
            if (stage == 0) accesses.Add(JobResourceAccess.Read(jobs._context.Structure));
            else
            {
                jobs._context.BeginQuery(); _ranges = NativeQueryRanges<T>.Rent(); jobs._query.Build(_ranges, jobs._context);
                jobs._context.Accesses(ref accesses);
            }
        }
        public void Execute()
        {
            JobHandle access = jobs._scheduler.GetCurrentScope(); ulong version = jobs._context.BeginValues(access);
            jobs.Execute(_ranges!, version, access); jobs._context.Merge();
        }
        public void Release() { _ranges?.Return(); _ranges = null; }
    }
    protected override bool HasCounter(Entity entity) => _context.Store.Has<SaveCounter>(entity);
    protected override SaveCounter ReadCounter(Entity entity) => _context.Store.Read<SaveCounter>(entity);
    protected override void CheckChanges(int count)
    {
        int changed = 0;
        foreach (Entity entity in Entities.Concat(ArchiveWorld.GrowthEntities))
            if (_context.Store.Has<SaveCounter>(entity))
            {
                var versions = _context.Store.Read<SaveNativeVersions>(entity);
                if (versions.Added > BeforeVersion || versions.Written != ArchiveWorld.Version)
                    throw new InvalidOperationException("Native tracked exact row versions differ.");
                changed++;
            }
        if (changed != count) throw new InvalidOperationException("Native tracked member count differs.");
        _context.CheckSummaries(ArchiveWorld.Version);
    }
    public override void Dispose() { _last.Complete(); _query.Dispose(); _query = default; _context.Dispose(); }
}
