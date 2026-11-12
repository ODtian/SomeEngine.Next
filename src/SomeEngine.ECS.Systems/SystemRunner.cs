using SomeEngine.ECS.Storage;
using SomeEngine.Job;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SomeEngine.ECS.Systems;

public sealed partial class SystemRunner(World world, Scheduler scheduler, int rangeRows = 1024)
{
    private readonly World _world = world;
    private readonly Scheduler _scheduler = scheduler;
    private readonly int _rangeRows = ((rangeRows + 63) >> 6) << 6;

    public void Execute<T1, T2, TAction>(Query query, ref TAction action, SystemAccesses? accesses = null)
        where T1 : struct where T2 : struct where TAction : struct, IComponentAction<T1, T2>
    {
        if (accesses is null) ExecuteQuery<T1, T2, TAction>(query, ref action);
        else ExecuteWithAccess<T1, T2, TAction>(query, ref action, accesses);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ExecuteQuery<T1, T2, TAction>(Query query, ref TAction action)
        where T1 : struct where T2 : struct where TAction : struct, IComponentAction<T1, T2>
    {
        if (query.World.Execution is WorldJobs jobs)
            query.ForEachWithExecution<T1, T2, TAction, WorldJobsQueryPolicy>(ref action, new(jobs));
        else query.ForEach<T1, T2, TAction>(ref action);
    }

    private readonly struct WorldJobsQueryPolicy(WorldJobs jobs) : IQueryExecutionPolicy
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void EnterQuery(Query query, ReadOnlySpan<int> writes) => jobs.EnterQuery(query, writes, false);

        public bool CanBorrowStructureRead
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => jobs.CanBorrowStructureRead;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Exit() => jobs.Exit();
    }

    private interface IExecuteAccessPolicy<TScope> where TScope : struct, IDisposable
    {
        TScope Open(ReadOnlySpan<WorldValueAccess> values);
        WorldJobs.WriteVersionScope WriteVersion();
    }

    private readonly struct WorldExecuteAccessPolicy(World world) : IExecuteAccessPolicy<WorldAccess>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public WorldAccess Open(ReadOnlySpan<WorldValueAccess> values) => world.OpenValues(values);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public WorldJobs.WriteVersionScope WriteVersion() => default;
    }

    private readonly struct WorldJobsExecuteAccessPolicy(WorldJobs jobs) : IExecuteAccessPolicy<WorldJobs.ValuesScope>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public WorldJobs.ValuesScope Open(ReadOnlySpan<WorldValueAccess> values) => jobs.OpenTypedValues(values);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public WorldJobs.WriteVersionScope WriteVersion() => jobs.WriteVersion(0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ExecuteWithAccess<T1, T2, TAction>(Query query, ref TAction action, SystemAccesses accesses)
        where T1 : struct where T2 : struct where TAction : struct, IComponentAction<T1, T2>
    {
        if (_world.Execution is WorldJobs jobs)
            ExecuteWithAccessCore<T1, T2, TAction, WorldJobsExecuteAccessPolicy, WorldJobs.ValuesScope>(query, ref action, accesses, new(jobs));
        else ExecuteWithAccessCore<T1, T2, TAction, WorldExecuteAccessPolicy, WorldAccess>(query, ref action, accesses, new(_world));
    }

    private static void ExecuteWithAccessCore<T1, T2, TAction, TAccess, TScope>(
        Query query, ref TAction action, SystemAccesses accesses, TAccess policy)
        where T1 : struct where T2 : struct where TAction : struct, IComponentAction<T1, T2>
        where TAccess : struct, IExecuteAccessPolicy<TScope> where TScope : struct, IDisposable
    {
        using TScope values = OpenExecuteAccess<T1, T2, TAccess, TScope>(accesses, policy);
        using var version = policy.WriteVersion();
        ExecuteQuery<T1, T2, TAction>(query, ref action);
    }

    private static TScope OpenExecuteAccess<T1, T2, TAccess, TScope>(SystemAccesses additional, TAccess policy, int third = -1)
        where T1 : struct where T2 : struct
        where TAccess : struct, IExecuteAccessPolicy<TScope> where TScope : struct, IDisposable
    {
        Span<WorldValueAccess> values = stackalloc WorldValueAccess[3 + additional.Values.Count];
        int count = 0;
        int first = ComponentType<T1>.TypeId, second = ComponentType<T2>.TypeId;
        values[count++] = new(first, WorldValueKind.Table, true, ComponentType<T1>.TracksWritesType, ComponentType<T1>.Value.UsesStructureWrites);
        if (second != first) values[count++] = new(second, WorldValueKind.Table, false, false, ComponentType<T2>.Value.UsesStructureWrites);
        if (third >= 0 && third != first && third != second)
            values[count++] = new(third, WorldValueKind.Table, false, false, ComponentTypes.Get(third).UsesStructureWrites);
        foreach (WorldValueAccess value in additional.Values) values[count++] = value;
        return policy.Open(values[..count]);
    }

    public void ExecuteParallel<T1, T2, TAction>(Query query, in TAction action, SystemAccesses? accesses = null)
        where T1 : struct where T2 : struct where TAction : struct, IComponentAction<T1, T2>
    {
        if (ComponentType<T1>.IsControlledType || ComponentType<T1>.IsIndexedType || ComponentType<T1>.IsGroupedType)
            throw new InvalidOperationException("Use the component's typed write entry.");
        if (ComponentType<T2>.IsSharedType) throw new InvalidOperationException("Use the range's shared value entry.");
        WorldJobs? jobs = _world.Execution as WorldJobs;
        SynchronousResourceAccess access = default;
        JobHandle handle = jobs?.CurrentAccess ?? default;
        bool acquired = jobs is not null && handle.Index == 0;
        if (acquired) { access = jobs!.BeginQueryAccess(); handle = access.Handle; }
        _world.BeginRead();
        QueryRanges ranges = QueryRanges.Rent();
        ulong version = 0;
        try
        {
            ranges.Build(query, _rangeRows);
            if (jobs is not null)
            {
                AccessBuilder<JobResourceAccess> required = new();
                try
                {
                    jobs.BuildQueryAccesses<T1, T2>(query, ranges, accesses, ref required, acquired);
                    if (acquired) _scheduler.AppendSynchronousAccess(handle, required);
                    else jobs.RequireQueryAccess(handle, required);
                }
                finally { required.Clear(); }
            }
            ranges.Select(query.SinceVersion);
            if (ranges.Count == 0) return;
            if (ComponentType<T1>.TracksWritesType) version = _world.NextVersion();
            WorldJobs? writes = ComponentType<T1>.TracksWritesType || accesses?.HasTrackedWrites == true ? jobs : null;
            RunComponentRanges<T1, T2, TAction>(ranges, action, query.SinceVersion, version, writes, handle, accesses?.RequiresSerial<T1>() == true);
        }
        finally
        {
            if (version != 0) ranges.Merge<T1>(version);
            ranges.Return();
            _world.EndRead();
            if (acquired) jobs!.EndQueryAccess(access);
        }
    }

    public JobHandle Schedule<T1, T2, TAction>(Query query, in TAction action,
        SystemAccesses? accesses = null, JobHandle dependency = default, bool parallel = true, SystemState? state = null)
        where T1 : struct where T2 : struct where TAction : struct, IComponentAction<T1, T2>
    {
        if (ComponentType<T1>.IsControlledType || ComponentType<T1>.IsIndexedType || ComponentType<T1>.IsGroupedType)
            throw new InvalidOperationException("Use the component's typed write entry.");
        if (ComponentType<T2>.IsSharedType) throw new InvalidOperationException("Use the range's shared value entry.");
        WorldJobs jobs = (WorldJobs)_world.Execution!;
        jobs.BeginUse();
        return _scheduler.ScheduleStages(new QueryStages<T1, T2, TAction>(this, jobs, query, action, accesses, parallel, state), dependency);
    }

    private void RunRanges<TWork>(QueryRanges ranges, in TWork work, JobHandle handle, bool serial, WorldJobs? jobs, ulong version)
        where TWork : struct, IRangeWork<QueryRange>
    {
        if (serial)
            _scheduler.ExecuteRanges(ranges.Items, 1, new SerialRanges<TWork>(work, ranges, jobs, version), handle);
        else if (_world.Execution is WorldJobs rangeJobs)
            _scheduler.ExecuteRanges(ranges.Items, ranges.Count, new PartitionedRanges<TWork>(rangeJobs, work), handle);
        else _scheduler.ExecuteRanges(ranges.Items, ranges.Count, work, handle);
    }

    private readonly struct PartitionedRanges<TWork>(WorldJobs jobs, TWork work) : IRangeWork<QueryRange>
        where TWork : struct, IRangeWork<QueryRange>
    {
        public void Execute(in QueryRange range)
        {
            using var scope = jobs.EnterRange(range);
            work.Execute(in range);
        }
    }

    private void RunComponentRanges<T1, T2, TAction>(QueryRanges ranges, in TAction action, ulong since, ulong version,
        WorldJobs? jobs, JobHandle handle, bool serial)
        where T1 : struct where T2 : struct where TAction : struct, IComponentAction<T1, T2>
    {
        if (!ComponentType<T1>.TracksWritesType && jobs is null && !ranges.HasFilters)
            RunRanges(ranges, new UntrackedComponentRangeWork<T1, T2, TAction>(action), handle, serial, null, 0);
        else RunRanges(ranges, new ComponentRangeWork<T1, T2, TAction>(action, since, version, jobs), handle, serial, jobs, version);
    }

    private readonly struct UntrackedComponentRangeWork<T1, T2, TAction>(TAction action) : IRangeWork<QueryRange>
        where T1 : struct where T2 : struct where TAction : struct, IComponentAction<T1, T2>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Execute(in QueryRange range)
        {
            ColumnEntry[] columns = range.Chunk.Columns;
            int start = range.Start, count = range.End - start;
            TAction local = action;
            local.Execute(MemoryMarshal.CreateSpan(ref ColumnEntry.At<T1>(ref columns[ComponentType<T1>.TypeId], start), count),
                MemoryMarshal.CreateReadOnlySpan(ref ColumnEntry.At<T2>(ref columns[ComponentType<T2>.TypeId], start), count));
        }
    }

    private readonly struct SerialRanges<TWork>(TWork work, QueryRanges ranges, WorldJobs? jobs, ulong version) : IRangeWork<QueryRange>
        where TWork : struct, IRangeWork<QueryRange>
    {
        public void Execute(in QueryRange item)
        {
            using var changes = jobs?.WriteVersion(version) ?? default;
            for (int i = 0; i < ranges.Count; i++) work.Execute(in ranges.Items[i]);
        }
    }

    private struct QueryStages<T1, T2, TAction>(SystemRunner runner, WorldJobs jobs, Query query,
        TAction action, SystemAccesses? additional, bool parallel, SystemState? state) : IStagedJob
        where T1 : struct where T2 : struct where TAction : struct, IComponentAction<T1, T2>
    {
        private QueryRanges? _ranges;
        public static int StageCount => 2;
        public void Prepare(int stage, ref AccessBuilder<JobResourceAccess> accesses)
        {
            if (stage == 0) accesses.Add(JobResourceAccess.Read(jobs.Structure));
            else
            {
                runner._world.BeginRead();
                _ranges = QueryRanges.Rent();
                _ranges.Build(query, runner._rangeRows);
                jobs.BuildQueryAccesses<T1, T2>(query, _ranges, additional, ref accesses);
            }
        }
        public void Execute()
        {
            if (state is not null)
            {
                state.CurrentSystemVersion = runner._world.Version;
                query = query.Since(state.LastSystemVersion);
            }
            QueryRanges ranges = _ranges!;
            ranges.Select(query.SinceVersion);
            if (ranges.Count == 0) return;
            ulong version = ComponentType<T1>.TracksWritesType ? runner._world.NextVersion() : 0;
            try
            {
                WorldJobs? writes = ComponentType<T1>.TracksWritesType || additional?.HasTrackedWrites == true ? jobs : null;
                runner.RunComponentRanges<T1, T2, TAction>(ranges, action, query.SinceVersion, version, writes, jobs.CurrentAccess,
                    !parallel || additional?.RequiresSerial<T1>() == true);
            }
            finally { if (version != 0) ranges.Merge<T1>(version); }
        }
        public void Release()
        {
            if (_ranges is { } ranges)
            {
                ranges.Return();
                runner._world.EndRead();
                _ranges = null;
            }
            jobs.EndUse();
        }
    }

    private readonly struct ComponentRangeWork<T1, T2, TAction>(TAction action, ulong since, ulong version, WorldJobs? jobs) : IRangeWork<QueryRange>
        where T1 : struct where T2 : struct where TAction : struct, IComponentAction<T1, T2>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Execute(in QueryRange range)
        {
            if (jobs is not null) ExecuteWithVersion(range);
            else ExecuteValues(range);
        }

        private void ExecuteWithVersion(in QueryRange range)
        {
            using var changes = jobs!.WriteVersion(version);
            ExecuteValues(range);
        }

        private void ExecuteValues(in QueryRange range)
        {
            if (range.Filter is null) { ExecuteContiguous(range); return; }
            Chunk chunk = range.Chunk;
            Span<T1> first = ColumnEntry.Span<T1>(ref chunk.Columns[ComponentType<T1>.TypeId], chunk.Count);
            ReadOnlySpan<T2> second = ColumnEntry.Span<T2>(ref chunk.Columns[ComponentType<T2>.TypeId], chunk.Count);
            TAction local = action;
            int next = range.Start;
            while (next < range.End && range.Filter.Next(chunk, since, ref next, out int start, out int count, range.End))
            {
                Run(chunk, start, count, first, second, ref local);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ExecuteContiguous(in QueryRange range)
        {
            Chunk chunk = range.Chunk;
            int start = range.Start, count = range.End - start;
            if (ComponentType<T1>.TracksWritesType)
            {
                ref ComponentChanges changes = ref chunk.Changes![chunk.Columns[ComponentType<T1>.TypeId].ColumnPlusOne - 1];
                changes.StampRange(start, count, version, ComponentType<T1>.TracksAddedType, publish: false);
            }
            TAction local = action;
            local.Execute(MemoryMarshal.CreateSpan(ref ColumnEntry.At<T1>(ref chunk.Columns[ComponentType<T1>.TypeId], start), count),
                MemoryMarshal.CreateReadOnlySpan(ref ColumnEntry.At<T2>(ref chunk.Columns[ComponentType<T2>.TypeId], start), count));
        }

        private void Run(Chunk chunk, int start, int count, Span<T1> first, ReadOnlySpan<T2> second, ref TAction local)
        {
            if (ComponentType<T1>.TracksWritesType)
            {
                ref ComponentChanges changes = ref chunk.Changes![chunk.Columns[ComponentType<T1>.TypeId].ColumnPlusOne - 1];
                changes.StampRange(start, count, version, ComponentType<T1>.TracksAddedType, publish: false);
            }
            local.Execute(first.Slice(start, count), second.Slice(start, count));
        }
    }
}
