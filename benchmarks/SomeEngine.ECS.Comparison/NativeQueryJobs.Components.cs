using SomeEngine.Job;
namespace SomeEngine.ECS.Comparison;

internal interface INativeTripleRange
{
    Span<Position> Positions { get; }
    ReadOnlySpan<Velocity> Velocities { get; }
    ReadOnlySpan<JobOrdinal> Ordinals { get; }
}
internal interface INativeTripleQuery<T> where T : struct, INativeTripleRange
{
    int EntityCount { get; }
    void Build(NativeQueryRanges<T> ranges);
    void Execute<TAction>(ref TAction action) where TAction : struct, IColumnPass<Position, Velocity, JobOrdinal>;
}

internal sealed class NativeQueryJobs<T, TQuery, TAction> : IQueryJobs
    where T : struct, INativeTripleRange where TQuery : struct, INativeTripleQuery<T>
    where TAction : struct, IColumnPass<Position, Velocity, JobOrdinal>
{
    private readonly Scheduler _scheduler;
    private readonly TQuery _query;
    private readonly TAction _action;
    private readonly bool _serial;
    private readonly JobResource _structure, _positions, _velocities, _ordinals, _extra;

    internal NativeQueryJobs(Scheduler scheduler, in TQuery query, in TAction action, bool serial)
    {
        _scheduler = scheduler; _query = query; _action = action; _serial = serial;
        _structure = scheduler.CreateResource("Native World structure");
        _positions = scheduler.CreateResource("Native World Position");
        _velocities = scheduler.CreateResource("Native World Velocity");
        _ordinals = scheduler.CreateResource("Native World JobOrdinal");
        _extra = serial ? scheduler.CreateResource("Native World random write") : default;
    }

    private void Accesses(NativeQueryRanges<T> ranges, ref AccessBuilder<JobResourceAccess> accesses)
    {
        if (ranges.Rows != 0 && ranges.Rows == _query.EntityCount)
        {
            accesses.Add(JobResourceAccess.Write(_positions));
            accesses.Add(JobResourceAccess.Read(_velocities));
            accesses.Add(JobResourceAccess.Read(_ordinals));
        }
        else for (int i = 0; i < ranges.Count; i++)
        {
            NativeQueryRange<T> range = ranges.Items[i];
            int end = range.Start + range.Count;
            while (i + 1 < ranges.Count && ranges.Items[i + 1].Chunk == range.Chunk)
            { NativeQueryRange<T> next = ranges.Items[++i]; end = next.Start + next.Count; }
            long start = ((long)range.Chunk << 32) + range.Start;
            int length = end - range.Start;
            accesses.Add(JobResourceAccess.Write(_positions, start, length));
            accesses.Add(JobResourceAccess.Read(_velocities, start, length));
            accesses.Add(JobResourceAccess.Read(_ordinals, start, length));
        }
        if (_serial) accesses.Add(JobResourceAccess.Write(_extra));
        else accesses.Add(JobResourceAccess.Read(_velocities));
    }

    public void Run(bool scheduled, bool parallel, bool dependent)
    {
        if (scheduled)
        {
            JobHandle first = _scheduler.ScheduleStages(new Stages(this, parallel));
            (dependent ? _scheduler.ScheduleStages(new Stages(this, parallel), first) : first).Complete();
            return;
        }
        using var access = _scheduler.AcquireSynchronousAccess([JobResourceAccess.Read(_structure)]);
        if (!parallel)
        {
            AccessBuilder<JobResourceAccess> values = new();
            try
            {
                values.Add(JobResourceAccess.Write(_positions)); values.Add(JobResourceAccess.Read(_velocities));
                values.Add(JobResourceAccess.Read(_ordinals));
                if (_serial) values.Add(JobResourceAccess.Write(_extra)); else values.Add(JobResourceAccess.Read(_velocities));
                _scheduler.AppendSynchronousAccess(access.Handle, values);
                TAction local = _action; _query.Execute(ref local);
            }
            finally { values.Clear(); }
            return;
        }
        NativeQueryRanges<T> ranges = NativeQueryRanges<T>.Rent();
        AccessBuilder<JobResourceAccess> required = new();
        try
        {
            _query.Build(ranges); Accesses(ranges, ref required);
            _scheduler.AppendSynchronousAccess(access.Handle, required);
            Execute(ranges, parallel, access.Handle);
        }
        finally { required.Clear(); ranges.Return(); }
    }

    private void Execute(NativeQueryRanges<T> ranges, bool parallel, JobHandle handle)
    {
        TAction local = _action;
        if (parallel && !_serial) _scheduler.ExecuteRanges(ranges.Items, ranges.Count, new RangeWork(local), handle);
        else _scheduler.ExecuteRanges(ranges.Items, 1, new SerialWork(ranges, local), handle);
    }
    private readonly struct RangeWork(TAction action) : IRangeWork<NativeQueryRange<T>>
    {
        public void Execute(in NativeQueryRange<T> range)
        { TAction local = action; local.Execute(range.Values.Positions, range.Values.Velocities, range.Values.Ordinals); }
    }
    private readonly struct SerialWork(NativeQueryRanges<T> ranges, TAction action) : IRangeWork<NativeQueryRange<T>>
    {
        public void Execute(in NativeQueryRange<T> ignored)
        {
            var work = new RangeWork(action);
            for (int i = 0; i < ranges.Count; i++) work.Execute(in ranges.Items[i]);
        }
    }
    private struct Stages(NativeQueryJobs<T, TQuery, TAction> jobs, bool parallel) : IStagedJob
    {
        private NativeQueryRanges<T>? _ranges;
        public static int StageCount => 2;
        public void Prepare(int stage, ref AccessBuilder<JobResourceAccess> accesses)
        {
            if (stage == 0) accesses.Add(JobResourceAccess.Read(jobs._structure));
            else
            { _ranges = NativeQueryRanges<T>.Rent(); jobs._query.Build(_ranges); jobs.Accesses(_ranges, ref accesses); }
        }
        public void Execute() => jobs.Execute(_ranges!, parallel, jobs._scheduler.GetCurrentScope());
        public void Release() { _ranges?.Return(); _ranges = null; }
    }
    public void Dispose()
    {
        _scheduler.ReleaseResource(_positions); _scheduler.ReleaseResource(_velocities);
        _scheduler.ReleaseResource(_ordinals); if (_serial) _scheduler.ReleaseResource(_extra);
        _scheduler.ReleaseResource(_structure);
    }
}
