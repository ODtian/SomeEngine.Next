using SomeEngine.ECS.Storage;
using SomeEngine.Job;

namespace SomeEngine.ECS.Systems;

public sealed partial class WorldJobs
{
    [ThreadStatic] private static WorldJobs? s_writeWorld;
    [ThreadStatic] private static ulong s_writeVersion;

    internal override ulong CurrentWriteVersion => ReferenceEquals(s_writeWorld, this)
        ? s_writeVersion != 0 ? s_writeVersion : s_writeVersion = _world.AdvanceVersion()
        : 0;

    private static WorldValueKind ChangeKind(WorldValueKind kind)
        => kind == WorldValueKind.Sparse ? WorldValueKind.SparseChanges : WorldValueKind.Changes;

    internal override JobHandle CurrentAccess => JobThreads.IsActive ? _scheduler.GetCurrentScope()
        : s_current is { } current && ReferenceEquals(current.Jobs, this) ? current.Handle : default;

    internal void BuildQueryAccesses<T1, T2>(Query query, QueryRanges ranges, SystemAccesses? additional,
        ref AccessBuilder<JobResourceAccess> accesses, bool wholeTypes = true, int third = -1) where T1 : struct where T2 : struct
    {
        int first = ComponentType<T1>.TypeId, second = ComponentType<T2>.TypeId;
        JobResource firstValues = Resource(WorldValueKind.Table, first);
        JobResource secondValues = Resource(WorldValueKind.Table, second);
        JobResource firstChanges = ComponentType<T1>.TracksWritesType ? Resource(WorldValueKind.Changes, first) : default;
        bool wholeFirst = wholeTypes && ranges.Count != 0 && query.CoversMatchedTypeLayouts(first);
        bool wholeSecond = wholeTypes && ranges.Count != 0 && first != second && query.CoversMatchedTypeLayouts(second);
        if (wholeFirst) accesses.Add(JobResourceAccess.Write(firstValues));
        if (wholeSecond) accesses.Add(JobResourceAccess.Read(secondValues));
        BuildQueryReads(query, ranges, first, second, third, ref accesses, wholeTypes);
        if (wholeFirst && (wholeSecond || first == second) && !ComponentType<T1>.TracksWritesType && query.Description.Changed.Count == 0)
        {
            if (additional is not null)
                foreach (WorldValueAccess value in additional.Values) AddValue(ref accesses, value);
            return;
        }
        for (int i = 0; i < ranges.Count; i++)
        {
            QueryRange range = ranges.Items[i];
            int end = range.End;
            while (i + 1 < ranges.Count && ReferenceEquals(ranges.Items[i + 1].Chunk, range.Chunk)) end = ranges.Items[++i].End;
            long start = ((long)range.Chunk.Id << 32) + range.Start;
            int length = end - range.Start;
            if (!wholeFirst) accesses.Add(JobResourceAccess.Write(firstValues, start, length));
            if (!wholeSecond && first != second) accesses.Add(JobResourceAccess.Read(secondValues, start, length));
            if (ComponentType<T1>.TracksWritesType)
                accesses.Add(JobResourceAccess.Write(firstChanges, range.Chunk.Id, 1));
            for (int type = 0; type < ComponentTypes.Count; type++)
                if (query.Description.Changed.Has(type) && !ComponentTypes.Get(type).UsesStructureWrites
                    && !(type == first && ComponentType<T1>.TracksWritesType))
                    accesses.Add(JobResourceAccess.Read(Resource(WorldValueKind.Changes, type), range.Chunk.Id, 1));
        }
        if (additional is not null)
            foreach (WorldValueAccess value in additional.Values) AddValue(ref accesses, value);
    }

    private void BuildQueryReads(Query query, QueryRanges ranges, int first, int second, int third,
        ref AccessBuilder<JobResourceAccess> accesses, bool wholeTypes)
    {
        if (ranges.Count == 0) return;
        foreach (int type in query.Description.Definition.AccessTypes)
        {
            if (type == first || type == second || type == third || ComponentTypes.Get(type).UsesStructureWrites) continue;
            JobResource values = Resource(WorldValueKind.Table, type);
            if (wholeTypes && query.CoversMatchedTypeLayouts(type))
            {
                accesses.Add(JobResourceAccess.Read(values));
                continue;
            }
            for (int i = 0; i < ranges.Count; i++)
            {
                QueryRange range = ranges.Items[i];
                int end = range.End;
                while (i + 1 < ranges.Count && ReferenceEquals(ranges.Items[i + 1].Chunk, range.Chunk)) end = ranges.Items[++i].End;
                accesses.Add(JobResourceAccess.Read(values, ((long)range.Chunk.Id << 32) + range.Start, end - range.Start));
            }
        }
    }

    internal void BuildQueryAccesses<T1, T2, T3>(Query query, QueryRanges ranges, SystemAccesses? additional,
        ref AccessBuilder<JobResourceAccess> accesses, bool wholeTypes = true) where T1 : struct where T2 : struct where T3 : struct
    {
        int third = ComponentType<T3>.TypeId;
        BuildQueryAccesses<T1, T2>(query, ranges, additional, ref accesses, wholeTypes, third);
        if (third == ComponentType<T1>.TypeId || third == ComponentType<T2>.TypeId) return;
        JobResource values = Resource(WorldValueKind.Table, third);
        if (wholeTypes && ranges.Count != 0 && query.CoversMatchedTypeLayouts(third)) { accesses.Add(JobResourceAccess.Read(values)); return; }
        for (int i = 0; i < ranges.Count; i++)
        {
            QueryRange range = ranges.Items[i];
            int end = range.End;
            while (i + 1 < ranges.Count && ReferenceEquals(ranges.Items[i + 1].Chunk, range.Chunk)) end = ranges.Items[++i].End;
            accesses.Add(JobResourceAccess.Read(values, ((long)range.Chunk.Id << 32) + range.Start, end - range.Start));
        }
    }

    internal void RequireQueryAccess(JobHandle handle, in AccessBuilder<JobResourceAccess> accesses)
    {
        if (!JobThreads.IsActive && s_current is { ValuesBound: false } current
            && ReferenceEquals(current.Jobs, this) && current.Access.Handle.Index != 0)
        {
            _scheduler.AppendSynchronousAccess(handle, accesses);
            current.ValuesBound = true;
        }
        _scheduler.RequireAccess(handle, JobResourceAccess.Read(_structure), false);
        for (int i = 0; i < accesses.Count; i++) _scheduler.RequireAccess(handle, accesses.Get(i), false);
    }

    internal WriteVersionScope WriteVersion(ulong version) => new(this, version);

    internal readonly struct WriteVersionScope : IDisposable
    {
        private readonly WorldJobs? _previous;
        private readonly ulong _version;
        private readonly bool _restore;
        internal WriteVersionScope(WorldJobs jobs, ulong version)
        {
            _previous = s_writeWorld;
            _version = s_writeVersion;
            _restore = !ReferenceEquals(s_writeWorld, jobs);
            if (_restore) { s_writeWorld = jobs; s_writeVersion = version; }
        }
        public void Dispose()
        {
            if (_restore) { s_writeWorld = _previous; s_writeVersion = _version; }
        }
    }
}
