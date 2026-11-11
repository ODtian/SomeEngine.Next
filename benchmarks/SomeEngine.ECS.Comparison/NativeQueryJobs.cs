using SomeEngine.Job;

namespace SomeEngine.ECS.Comparison;

internal interface IQueryJobs : IDisposable
{
    void Run(bool scheduled, bool parallel, bool dependent);
}

internal interface INativeQueryRanges<T> where T : struct, INativeQueryRange
{
    void Build(NativeQueryRanges<T> ranges);
    void Execute();
    int EntityCount { get; }
}

internal interface INativeQueryRange { void Execute(); }

internal readonly struct NativeQueryRange<T>(T values, int chunk, int start, int count) where T : struct
{
    internal readonly T Values = values;
    internal readonly int Chunk = chunk, Start = start, Count = count;
}

internal sealed class NativeQueryRanges<T> where T : struct
{
    [ThreadStatic] private static NativeQueryRanges<T>[]? s_free;
    [ThreadStatic] private static int s_freeCount;
    internal NativeQueryRange<T>[] Items = new NativeQueryRange<T>[16];
    internal int Count;
    internal int Rows;

    internal static NativeQueryRanges<T> Rent()
    {
        if (s_freeCount == 0) return new();
        int index = --s_freeCount;
        NativeQueryRanges<T> ranges = s_free![index];
        s_free[index] = null!;
        return ranges;
    }
    internal void Add(in T values, int chunk, int start, int count)
    {
        if (Count == Items.Length) Array.Resize(ref Items, Count * 2);
        Items[Count++] = new(values, chunk, start, count);
        Rows += count;
    }
    internal void Return()
    {
        Array.Clear(Items, 0, Count);
        Count = 0;
        Rows = 0;
        NativeQueryRanges<T>[] free = s_free ??= new NativeQueryRanges<T>[4];
        if (s_freeCount == free.Length) { Array.Resize(ref free, free.Length * 2); s_free = free; }
        free[s_freeCount++] = this;
    }
}

internal sealed class NativeQueryJobs<T, TQuery> : IQueryJobs
    where T : struct, INativeQueryRange where TQuery : struct, INativeQueryRanges<T>
{
    private readonly Scheduler _scheduler;
    private TQuery _query;
    private readonly JobResource _structure, _positions, _velocities;
    internal JobResource Structure => _structure;

    internal NativeQueryJobs(Scheduler scheduler, in TQuery query)
    {
        _scheduler = scheduler;
        _query = query;
        _structure = scheduler.CreateResource("Native World structure");
        _positions = scheduler.CreateResource("Native World Position");
        _velocities = scheduler.CreateResource("Native World Velocity");
    }
    private void Accesses(NativeQueryRanges<T> ranges, ref AccessBuilder<JobResourceAccess> accesses)
    {
        if (ranges.Rows != 0 && ranges.Rows == _query.EntityCount)
        {
            accesses.Add(JobResourceAccess.Write(_positions));
            accesses.Add(JobResourceAccess.Read(_velocities));
            return;
        }
        for (int i = 0; i < ranges.Count; i++)
        {
            NativeQueryRange<T> range = ranges.Items[i];
            int end = range.Start + range.Count;
            while (i + 1 < ranges.Count && ranges.Items[i + 1].Chunk == range.Chunk)
            {
                NativeQueryRange<T> next = ranges.Items[++i];
                end = next.Start + next.Count;
            }
            long start = ((long)range.Chunk << 32) + range.Start;
            accesses.Add(JobResourceAccess.Write(_positions, start, end - range.Start));
            accesses.Add(JobResourceAccess.Read(_velocities, start, end - range.Start));
        }
    }
    public void Run(bool scheduled, bool parallel, bool dependent)
    {
        if (scheduled)
        {
            JobHandle first = _scheduler.ScheduleStages(new Stages(this, parallel));
            (dependent ? _scheduler.ScheduleStages(new Stages(this, parallel), first) : first).Complete();
            return;
        }
        if (!parallel)
        {
            ReadOnlySpan<JobResourceAccess> serialAccesses = stackalloc JobResourceAccess[]
            {
                JobResourceAccess.Read(_structure), JobResourceAccess.Write(_positions), JobResourceAccess.Read(_velocities)
            };
            using var serialAccess = _scheduler.AcquireSynchronousAccess(serialAccesses);
            _query.Execute();
            return;
        }
        using var access = _scheduler.AcquireSynchronousAccess([JobResourceAccess.Read(_structure)]);
        NativeQueryRanges<T> ranges = NativeQueryRanges<T>.Rent();
        AccessBuilder<JobResourceAccess> required = new();
        try
        {
            _query.Build(ranges);
            Accesses(ranges, ref required);
            _scheduler.AppendSynchronousAccess(access.Handle, required);
            Execute(ranges, parallel, access.Handle);
        }
        finally
        {
            required.Clear(); ranges.Return();
        }
    }
    private void Execute(NativeQueryRanges<T> ranges, bool parallel, JobHandle handle)
    {
        if (parallel) _scheduler.ExecuteRanges(ranges.Items, ranges.Count, new RangeWork(), handle);
        else for (int i = 0; i < ranges.Count; i++) ranges.Items[i].Values.Execute();
    }
    private readonly struct RangeWork : IRangeWork<NativeQueryRange<T>>
    {
        public void Execute(in NativeQueryRange<T> range) => range.Values.Execute();
    }
    private struct Stages(NativeQueryJobs<T, TQuery> jobs, bool parallel) : IStagedJob
    {
        private NativeQueryRanges<T>? _ranges;
        public static int StageCount => 2;
        public void Prepare(int stage, ref AccessBuilder<JobResourceAccess> accesses)
        {
            if (stage == 0) accesses.Add(JobResourceAccess.Read(jobs._structure));
            else
            {
                _ranges = NativeQueryRanges<T>.Rent();
                jobs._query.Build(_ranges);
                jobs.Accesses(_ranges, ref accesses);
            }
        }
        public void Execute()
        {
            jobs.Execute(_ranges!, parallel, jobs._scheduler.GetCurrentScope());
        }
        public void Release()
        {
            _ranges?.Return(); _ranges = null;
        }
    }

    internal JobHandle ScheduleSystem(NativeSystemProgress state, bool parallel, JobHandle dependency)
        => _scheduler.ScheduleStages(new SystemStages(this, state, parallel), dependency);

    private struct SystemStages(NativeQueryJobs<T, TQuery> jobs, NativeSystemProgress state, bool parallel) : IStagedJob
    {
        private NativeQueryRanges<T>? _ranges;
        public static int StageCount => 2;
        public void Prepare(int stage, ref AccessBuilder<JobResourceAccess> accesses)
        {
            if (stage == 0) accesses.Add(JobResourceAccess.Read(jobs._structure));
            else
            { _ranges = NativeQueryRanges<T>.Rent(); jobs._query.Build(_ranges); jobs.Accesses(_ranges, ref accesses); }
        }
        public void Execute()
        {
            state.Current = state.Version();
            jobs.Execute(_ranges!, parallel, jobs._scheduler.GetCurrentScope());
        }
        public void Release() { _ranges?.Return(); _ranges = null; }
    }
    public void Dispose()
    {
        _scheduler.ReleaseResource(_positions);
        _scheduler.ReleaseResource(_velocities);
        _scheduler.ReleaseResource(_structure);
        _query = default;
    }
}
