using SomeEngine.ECS.Storage;
using SomeEngine.Job;

namespace SomeEngine.ECS.Systems;

public sealed partial class SystemRunner
{
    /// <summary>Uses the same required components and observation window as the typed action's execution.</summary>
    public static Query Bind<TAction>(Query query) where TAction : struct, IChunkAction => ChunkActionTypes<TAction>.Bind(query);

    private readonly int _chunkRangeRows = rangeRows;

    private int ChunkRangeRows<TAction>() where TAction : struct, IChunkAction
        => ChunkActionTypes<TAction>.TrackedWrites.Length == 0 ? _chunkRangeRows : _rangeRows;

    public SystemRunner(World world, int rangeRows = 1024)
        : this(world, world.Execution is WorldJobs jobs ? jobs.Scheduler
            : throw new InvalidOperationException("Connect WorldJobs before scheduling generated work."), rangeRows) { }

    public void ExecuteParallel<TAction>(in TAction action, Query? query = null) where TAction : struct, IChunkAction
        => ExecuteParallel(query ?? _world.Query(ChunkActionTypes<TAction>.Query), action);

    public JobHandle Schedule<TAction>(in TAction action, JobHandle dependency = default, bool parallel = true,
        Query? query = null) where TAction : struct, IChunkAction
        => Schedule(query ?? _world.Query(ChunkActionTypes<TAction>.Query), action, dependency, parallel);

    public static void Execute<TAction>(World world, in TAction action, Query? query = null)
        where TAction : struct, IChunkAction
    {
        Query selected = ChunkActionTypes<TAction>.Bind(query ?? world.Query(ChunkActionTypes<TAction>.Query));
        SystemAccesses? additional = TAction.AdditionalAccesses;
        using WorldAccess structure = world.OpenStructure(false);
        using WorldAccess values = OpenChunkValues<TAction>(world, selected, additional);
        world.BeginRead();
        QueryRanges ranges = QueryRanges.Rent(); ulong version = 0;
        try
        {
            ranges.Build(selected, 1024); ranges.Select(selected.SinceVersion);
            if (ranges.Count == 0) return;
            if (world.Execution is WorldJobs jobs)
                AcquireChunkResources(jobs, ranges, jobs.CurrentAccess, !JobThreads.IsActive, selected.SinceVersion, action);
            bool scoped = ChunkActionTypes<TAction>.TrackedWrites.Length != 0 || additional?.HasTrackedWrites == true;
            if (ChunkActionTypes<TAction>.TrackedWrites.Length != 0) version = world.NextVersion();
            using var batch = scoped ? world.BeginBatchVersion(version) : default;
            var work = new ChunkActionWork<TAction>(world, action, selected.SinceVersion, version, scoped);
            for (int i = 0; i < ranges.Count; i++) work.Execute(in ranges.Items[i]);
        }
        finally
        {
            if (version != 0) MergeChunkWrites<TAction>(ranges, version);
            ranges.Return(); world.EndRead();
        }
    }

    public void ExecuteParallel<TAction>(Query query, in TAction action)
        where TAction : struct, IChunkAction
    {
        query = ChunkActionTypes<TAction>.Bind(query);
        SystemAccesses? additional = TAction.AdditionalAccesses;
        WorldJobs? jobs = _world.Execution as WorldJobs;
        SynchronousResourceAccess access = default; JobHandle handle = jobs?.CurrentAccess ?? default;
        bool acquired = jobs is not null && handle.Index == 0;
        bool schedule = false, resourceWrites = false;
        if (acquired) { access = jobs!.BeginQueryAccess(); handle = access.Handle; }
        _world.BeginRead(); QueryRanges ranges = QueryRanges.Rent(); ulong version = 0;
        try
        {
            ranges.Build(query, ChunkRangeRows<TAction>());
            if (jobs is not null)
            {
                AccessBuilder<JobResourceAccess> required = new();
                try
                {
                    jobs.BuildChunkAccesses<TAction>(query, ranges, additional, ref required, acquired);
                    if (acquired) _scheduler.AppendSynchronousAccess(handle, required);
                    else jobs.RequireQueryAccess(handle, required);
                }
                finally { required.Clear(); }
                resourceWrites = AcquireChunkResources(jobs, ranges, handle, acquired, query.SinceVersion, action);
            }
            schedule = acquired && resourceWrites;
            if (!schedule)
            {
                ranges.Select(query.SinceVersion); if (ranges.Count == 0) return;
                if (ChunkActionTypes<TAction>.TrackedWrites.Length != 0) version = _world.NextVersion();
                RunChunkAction(ranges, action, query.SinceVersion, version, handle,
                    resourceWrites || ChunkActionTypes<TAction>.Serial(additional));
            }
        }
        finally
        {
            if (version != 0) MergeChunkWrites<TAction>(ranges, version);
            ranges.Return(); _world.EndRead();
            if (acquired) jobs!.EndQueryAccess(access);
        }
        if (schedule) Schedule(query, action).Complete();
    }

    public JobHandle Schedule<TAction>(Query query, in TAction action, JobHandle dependency = default,
        bool parallel = true, SystemState? state = null) where TAction : struct, IChunkAction
    {
        query = ChunkActionTypes<TAction>.Bind(query);
        WorldJobs jobs = (WorldJobs)_world.Execution!; jobs.BeginUse();
        return _scheduler.ScheduleStages(new ChunkStages<TAction>(this, jobs, query, action, parallel, state), dependency);
    }

    private static WorldAccess OpenChunkValues<TAction>(World world, Query query, SystemAccesses? additional)
        where TAction : struct, IChunkAction
    {
        Span<WorldValueAccess> values = stackalloc WorldValueAccess[ChunkActionTypes<TAction>.Components.Length
            + (additional?.Values.Count ?? 0) + ComponentTypes.Count];
        int count = 0;
        foreach (ChunkAccess access in ChunkActionTypes<TAction>.Components)
            values[count++] = new(access.Type, WorldValueKind.Table, access.WritesValue, access.Tracked, access.Structural);
        for (int type = 0; type < ComponentTypes.Count; type++)
            if (query.Description.Changed.Has(type) && !ChunkActionTypes<TAction>.Writes.Contains(type)
                && !ComponentTypes.Get(type).UsesStructureWrites)
                values[count++] = new(type, WorldValueKind.Changes, false, false);
        if (additional is not null) foreach (WorldValueAccess access in additional.Values) values[count++] = access;
        return world.OpenValues(values[..count]);
    }

    internal static void CollectChunkResources<TAction>(Scheduler scheduler, QueryRanges ranges,
        ref AccessBuilder<JobResourceAccess> accesses, ulong since, in TAction action) where TAction : struct, IChunkAction
    {
        var resources = new JobResourceAccessBuilder(scheduler, ref accesses);
        action.CollectAdditionalResources(ref resources);
        for (int index = 0; index < ranges.Count; index++)
            CollectChunkRangeResources(in ranges.Items[index], ref resources, since, action);
    }

    private static void CollectChunkRangeResources<TAction>(in QueryRange range,
        ref JobResourceAccessBuilder resources, ulong since, in TAction action) where TAction : struct, IChunkAction
    {
        if (range.Filter is null)
        {
            var rows = new SystemChunk(range.Chunk, range.Start, range.End - range.Start, 0);
            TAction.CollectResources(rows, ref resources);
            action.CollectRangeResources(rows, ref resources);
        }
        else
        {
            int next = range.Start;
            while (next < range.End && range.Filter.Next(range.Chunk, since, ref next,
                out int start, out int count, range.End))
            {
                var rows = new SystemChunk(range.Chunk, start, count, 0);
                TAction.CollectResources(rows, ref resources);
                action.CollectRangeResources(rows, ref resources);
            }
        }
    }

    private static bool AcquireChunkResources<TAction>(WorldJobs jobs, QueryRanges ranges, JobHandle access,
        bool append, ulong since, in TAction action) where TAction : struct, IChunkAction
    {
        AccessBuilder<JobResourceAccess> resources = new();
        try
        {
            CollectChunkResources(jobs.Scheduler, ranges, ref resources, since, action);
            if (resources.Count == 0) return false;
            if (append) jobs.Scheduler.AppendSynchronousAccess(access, resources);
            else jobs.RequireQueryAccess(access, resources);
            return HasResourceWrites(resources);
        }
        finally { resources.Clear(); }
    }

    private static bool HasResourceWrites(in AccessBuilder<JobResourceAccess> resources)
    {
        for (int index = 0; index < resources.Count; index++)
            if (resources.Get(index).Mode != JobAccessMode.Read) return true;
        return false;
    }

    private readonly record struct ChunkResourceRange(QueryRange Range, JobResourceAccess[] Accesses);

    private ChunkResourceRange[] BindResourceRanges<TAction>(WorldJobs jobs, Query query, QueryRanges ranges,
        ulong since, in AccessBuilder<JobResourceAccess> captured, in TAction action) where TAction : struct, IChunkAction
    {
        var result = new ChunkResourceRange[ranges.Count];
        QueryRanges selected = QueryRanges.Rent();
        selected.Count = 1;
        try
        {
            for (int index = 0; index < ranges.Count; index++)
            {
                QueryRange range = selected.Items[0] = ranges.Items[index];
                AccessBuilder<JobResourceAccess> accesses = new();
                try
                {
                    accesses.Add(JobResourceAccess.Read(jobs.Structure));
                    jobs.BuildChunkAccesses<TAction>(query, selected, TAction.AdditionalAccesses, ref accesses, false);
                    for (int item = 0; item < captured.Count; item++) accesses.Add(captured.Get(item));
                    var resources = new JobResourceAccessBuilder(_scheduler, ref accesses);
                    CollectChunkRangeResources(range, ref resources, since, action);
                    var values = new JobResourceAccess[accesses.Count];
                    for (int item = 0; item < values.Length; item++) values[item] = accesses.Get(item);
                    result[index] = new(range, values);
                }
                finally { accesses.Clear(); }
            }
            return result;
        }
        finally { selected.Return(); }
    }

    private readonly struct ChunkRangeJob<TAction>(WorldJobs jobs, ChunkActionWork<TAction> work,
        QueryRange range, ulong version) : IJob where TAction : struct, IChunkAction
    {
        public void Execute()
        {
            using var scope = jobs.EnterRange(range);
            try { work.Execute(in range); }
            finally
            {
                if (version != 0)
                    foreach (int type in ChunkActionTypes<TAction>.TrackedWrites)
                        if ((uint)type < (uint)range.Chunk.Columns.Length && range.Chunk.Columns[type].ColumnPlusOne != 0)
                            range.Chunk.Changes![range.Chunk.Columns[type].ColumnPlusOne - 1]
                                .MergeWords(range.Start >> 6, (range.End + 63) >> 6, version);
            }
        }
    }

    private void RunChunkAction<TAction>(QueryRanges ranges, in TAction action, ulong since, ulong version,
        JobHandle handle, bool serial) where TAction : struct, IChunkAction
    {
        bool scoped = ChunkActionTypes<TAction>.TrackedWrites.Length != 0 || TAction.AdditionalAccesses?.HasTrackedWrites == true;
        var work = new ChunkActionWork<TAction>(_world, action, since, version, scoped);
        if (serial) _scheduler.ExecuteRanges(ranges.Items, 1, new ChunkActionSequence<TAction>(_world, work, ranges, version, scoped), handle);
        else if (_world.Execution is WorldJobs rangeJobs)
            _scheduler.ExecuteRanges(ranges.Items, ranges.Count, new PartitionedRanges<ChunkActionWork<TAction>>(rangeJobs, work), handle);
        else _scheduler.ExecuteRanges(ranges.Items, ranges.Count, work, handle);
    }

    private static void MergeChunkWrites<TAction>(QueryRanges ranges, ulong version) where TAction : struct, IChunkAction
    {
        foreach (int type in ChunkActionTypes<TAction>.TrackedWrites)
            for (int i = 0; i < ranges.Count; i++)
            {
                QueryRange range = ranges.Items[i]; Chunk chunk = range.Chunk;
                if ((uint)type >= (uint)chunk.Columns.Length || chunk.Columns[type].ColumnPlusOne == 0) continue;
                chunk.Changes![chunk.Columns[type].ColumnPlusOne - 1].MergeWords(range.Start >> 6, (range.End + 63) >> 6, version);
            }
    }

    private readonly struct ChunkActionSequence<TAction>(World world, ChunkActionWork<TAction> work,
        QueryRanges ranges, ulong version, bool scoped) : IRangeWork<QueryRange> where TAction : struct, IChunkAction
    {
        public void Execute(in QueryRange item)
        {
            using var changes = scoped ? world.BeginBatchVersion(version) : default;
            for (int i = 0; i < ranges.Count; i++) work.Execute(in ranges.Items[i]);
        }
    }

    private readonly struct ChunkActionWork<TAction>(World world, TAction action, ulong since, ulong version, bool scoped)
        : IRangeWork<QueryRange> where TAction : struct, IChunkAction
    {
        public void Execute(in QueryRange range)
        {
            using var changes = scoped ? world.BeginBatchVersion(version) : default;
            TAction local = action;
            if (range.Filter is null) { local.Execute(new(range.Chunk, range.Start, range.End - range.Start, version)); return; }
            int next = range.Start;
            while (next < range.End && range.Filter.Next(range.Chunk, since, ref next, out int start, out int count, range.End))
            {
                local.Execute(new(range.Chunk, start, count, version));
            }
        }
    }

    private struct ChunkStages<TAction>(SystemRunner runner, WorldJobs jobs, Query query, TAction action,
        bool parallel, SystemState? state) : IStagedJob where TAction : struct, IChunkAction
    {
        private QueryRanges? _ranges;
        private ChunkResourceRange[]? _resourceRanges;
        public static int StageCount => 3;
        public void Prepare(int stage, ref AccessBuilder<JobResourceAccess> accesses)
        {
            if (stage == 0) accesses.Add(JobResourceAccess.Read(jobs.Structure));
            else if (stage == 1)
            {
                runner._world.BeginRead(); _ranges = QueryRanges.Rent(); _ranges.Build(query, runner.ChunkRangeRows<TAction>());
                jobs.BuildChunkAccesses<TAction>(query, _ranges, TAction.AdditionalAccesses, ref accesses);
            }
            else
            {
                ulong since = state?.LastSystemVersion ?? query.SinceVersion;
                AccessBuilder<JobResourceAccess> captured = new();
                try
                {
                    var resources = new JobResourceAccessBuilder(jobs.Scheduler, ref captured);
                    action.CollectAdditionalResources(ref resources);
                    for (int index = 0; index < captured.Count; index++) accesses.Add(captured.Get(index));
                    var all = new JobResourceAccessBuilder(jobs.Scheduler, ref accesses);
                    for (int index = 0; index < _ranges!.Count; index++)
                        CollectChunkRangeResources(_ranges.Items[index], ref all, since, action);
                    if (parallel && !ChunkActionTypes<TAction>.Serial(TAction.AdditionalAccesses) && HasResourceWrites(accesses))
                    {
                        _ranges.Select(since);
                        _resourceRanges = runner.BindResourceRanges<TAction>(jobs, query, _ranges, since, captured, action);
                        accesses.Clear();
                    }
                }
                finally { captured.Clear(); }
            }
        }
        public void Execute()
        {
            if (state is not null) { state.CurrentSystemVersion = runner._world.Version; query = query.Since(state.LastSystemVersion); }
            QueryRanges ranges = _ranges!; ranges.Select(query.SinceVersion); if (ranges.Count == 0) return;
            ulong version = ChunkActionTypes<TAction>.TrackedWrites.Length != 0 ? runner._world.NextVersion() : 0;
            if (_resourceRanges is { } resourceRanges)
            {
                bool scoped = ChunkActionTypes<TAction>.TrackedWrites.Length != 0 || TAction.AdditionalAccesses?.HasTrackedWrites == true;
                var work = new ChunkActionWork<TAction>(runner._world, action, query.SinceVersion, version, scoped);
                foreach (ChunkResourceRange range in resourceRanges)
                    runner._scheduler.Schedule(new ChunkRangeJob<TAction>(jobs, work, range.Range, version), range.Accesses);
                return;
            }
            try { runner.RunChunkAction(ranges, action, query.SinceVersion, version, jobs.CurrentAccess,
                !parallel || ChunkActionTypes<TAction>.Serial(TAction.AdditionalAccesses)); }
            finally { if (version != 0) MergeChunkWrites<TAction>(ranges, version); }
        }
        public void Release()
        {
            if (_ranges is { } ranges) { ranges.Return(); runner._world.EndRead(); _ranges = null; }
            _resourceRanges = null;
            jobs.EndUse();
        }
    }
}
