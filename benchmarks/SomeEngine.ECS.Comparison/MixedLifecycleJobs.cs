using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.ECS.Comparison;

internal sealed class LifecycleRows { internal int Count; }

internal abstract class MixedLifecycleJobs(int count) : IDisposable
{
    internal readonly LifecycleRows Rows = new();
    private int _entityCount = count;
    protected JobHandle Last;
    protected int Runs;
    internal abstract void Schedule();
    internal void RecordGrowth(int count) => _entityCount += count;
    internal void Complete() => Last.Complete();
    internal void CheckCompleted()
    {
        if (!Last.IsCompleted || Rows.Count != Runs * _entityCount)
            throw new InvalidOperationException("Mixed lifecycle row completion differs.");
    }
    public abstract void Dispose();
}

internal sealed partial class SourceMixedLifecycleJobs : MixedLifecycleJobs
{
    private readonly Query _query;
    private readonly SystemRunner _runner;
    internal SourceMixedLifecycleJobs(World world, Scheduler scheduler, int count) : base(count)
    { _query = world.Query<Position, Velocity>(); _runner = new(world, scheduler); }
    internal override void Schedule()
    { Last = _runner.Schedule(_query, new Move(Rows), Last); Runs++; }
    private readonly partial struct Move(LifecycleRows rows) : IJobChunk
    {
        public void Execute(Span<Position> positions, ReadOnlySpan<Velocity> velocities)
        { ComponentMath.Move(positions, velocities); Interlocked.Add(ref rows.Count, positions.Length); }
        public void CollectAdditionalResources(ref JobResourceAccessBuilder resources) => resources.Write(rows);
    }
    public override void Dispose() => Complete();
}

internal readonly struct LifecycleRange<T>(T values, int count, LifecycleRows rows) : INativeQueryRange
    where T : struct, INativeQueryRange
{
    public void Execute() { values.Execute(); Interlocked.Add(ref rows.Count, count); }
}

internal readonly struct LifecycleQuery<T, TQuery>(TQuery query, LifecycleRows rows) : INativeQueryRanges<LifecycleRange<T>>
    where T : struct, INativeQueryRange where TQuery : struct, INativeQueryRanges<T>
{
    public int EntityCount => query.EntityCount;
    public void Execute() { query.Execute(); Interlocked.Add(ref rows.Count, query.EntityCount); }
    public void Build(NativeQueryRanges<LifecycleRange<T>> ranges)
    {
        var native = NativeQueryRanges<T>.Rent();
        try
        {
            query.Build(native);
            for (int i = 0; i < native.Count; i++)
            {
                var range = native.Items[i];
                ranges.Add(new(range.Values, range.Count, rows), range.Chunk, range.Start, range.Count);
            }
        }
        finally { native.Return(); }
    }
}

internal sealed class NativeMixedLifecycleJobs<T, TQuery> : MixedLifecycleJobs
    where T : struct, INativeQueryRange where TQuery : struct, INativeQueryRanges<T>
{
    private readonly NativeQueryJobs<LifecycleRange<T>, LifecycleQuery<T, TQuery>> _jobs;
    private readonly NativeSystemProgress _progress;
    internal NativeMixedLifecycleJobs(Scheduler scheduler, TQuery query, Func<ulong> version, int count) : base(count)
    { _jobs = new(scheduler, new(query, Rows)); _progress = new(version); }
    internal override void Schedule() { Last = _jobs.ScheduleSystem(_progress, true, Last); Runs++; }
    public override void Dispose() { Complete(); _jobs.Dispose(); }
}
