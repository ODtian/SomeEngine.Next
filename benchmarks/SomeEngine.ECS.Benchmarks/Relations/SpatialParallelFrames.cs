using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;
using SomeEngine.Job;
using SomeEngine.Physics;

namespace SomeEngine.ECS.Benchmarks.Relations.Spatial;

public sealed class SpatialContactTotals : IDisposable
{
    private readonly Scheduler _scheduler;
    private readonly JobResourceToken _resource;

    public SpatialContactTotals(Scheduler scheduler, int entityCapacity)
    {
        _scheduler = scheduler;
        _resource = scheduler.CreateResourceToken("Spatial contact totals");
        ContactCounts = new int[entityCapacity];
        PenetrationDepths = new float[entityCapacity];
    }

    public int[] ContactCounts { get; }
    public float[] PenetrationDepths { get; }
    public JobResourceAccess ReadAccess => JobResourceAccess.Read(_resource);
    internal JobResourceAccess WriteAccess => JobResourceAccess.Write(_resource);

    internal void Consume(World world, QueryHandle bodies, ReadOnlySpan<(Entity, Entity)> pairs)
    {
        Array.Clear(ContactCounts);
        Array.Clear(PenetrationDepths);
        SpatialCollisionConsumption.Measure(world, bodies, pairs, ContactCounts, PenetrationDepths);
    }

    public void Dispose() => _scheduler.ReleaseResourceToken(_resource);
}

public static partial class SpatialCollisionFrames
{
    public static JobHandle SchedulePairs<TGeometry, TBoundsReader, TFilter, TGroupResults>(
        CollisionMaintenance<TGeometry, TBoundsReader, TFilter, TGroupResults> maintenance,
        CollisionPairBuffer pairs, JobHandle dependency = default)
        where TGeometry : struct
        where TBoundsReader : struct, ICollisionBoundsReader<TGeometry>
        where TFilter : struct, ISpatialFilter<TGroupResults>
        where TGroupResults : unmanaged => SchedulePairsCore(maintenance, pairs, false, dependency);

    public static JobHandle SchedulePairsParallel<TGeometry, TBoundsReader, TFilter, TGroupResults>(
        CollisionMaintenance<TGeometry, TBoundsReader, TFilter, TGroupResults> maintenance,
        CollisionPairBuffer pairs, JobHandle dependency = default)
        where TGeometry : struct
        where TBoundsReader : struct, ICollisionBoundsReader<TGeometry>
        where TFilter : struct, ISpatialFilter<TGroupResults>
        where TGroupResults : unmanaged => SchedulePairsCore(maintenance, pairs, true, dependency);

    private static JobHandle SchedulePairsCore<TGeometry, TBoundsReader, TFilter, TGroupResults>(
        CollisionMaintenance<TGeometry, TBoundsReader, TFilter, TGroupResults> maintenance,
        CollisionPairBuffer pairs, bool parallel, JobHandle dependency)
        where TGeometry : struct
        where TBoundsReader : struct, ICollisionBoundsReader<TGeometry>
        where TFilter : struct, ISpatialFilter<TGroupResults>
        where TGroupResults : unmanaged
    {
        var job = new PairJob<SpatialCandidates<TGeometry, TBoundsReader, TFilter, TGroupResults>>(maintenance.Query, pairs, parallel);
        ReadOnlySpan<JobResourceAccess> output = [pairs.FillAccess];
        return maintenance.ScheduleRead(in job, output, dependency);
    }

    public static JobHandle ScheduleConsume<TGeometry, TBoundsReader, TFilter, TGroupResults>(
        CollisionMaintenance<TGeometry, TBoundsReader, TFilter, TGroupResults> maintenance,
        CollisionPairBuffer pairs, SpatialContactTotals totals, JobHandle dependency = default)
        where TGeometry : struct
        where TBoundsReader : struct, ICollisionBoundsReader<TGeometry>
        where TFilter : struct, ISpatialFilter<TGroupResults>
        where TGroupResults : unmanaged
    {
        var job = new ConsumeJob(maintenance.Query.Enumerable.World, maintenance.Query.Enumerable.GetQuery(0), pairs, totals);
        ReadOnlySpan<JobResourceAccess> output = [pairs.ReadAccess, totals.WriteAccess];
        return maintenance.ScheduleRead(in job, output, dependency);
    }

    public static JobHandle ScheduleParallel<TGeometry, TBoundsReader, TFilter, TGroupResults>(
        CollisionMaintenance<TGeometry, TBoundsReader, TFilter, TGroupResults> maintenance,
        CollisionPairBuffer pairs, SpatialContactTotals totals, JobHandle dependency = default)
        where TGeometry : struct
        where TBoundsReader : struct, ICollisionBoundsReader<TGeometry>
        where TFilter : struct, ISpatialFilter<TGroupResults>
        where TGroupResults : unmanaged =>
        ScheduleConsume(maintenance, pairs, totals, SchedulePairsParallel(maintenance, pairs, dependency));

    public static JobHandle SchedulePairs(HandwrittenSpatialMaintenance maintenance, CollisionPairBuffer pairs,
        JobHandle dependency = default) => SchedulePairsCore(maintenance, pairs, false, dependency);

    public static JobHandle SchedulePairsParallel(HandwrittenSpatialMaintenance maintenance, CollisionPairBuffer pairs,
        JobHandle dependency = default) => SchedulePairsCore(maintenance, pairs, true, dependency);

    private static JobHandle SchedulePairsCore(HandwrittenSpatialMaintenance maintenance, CollisionPairBuffer pairs,
        bool parallel, JobHandle dependency)
    {
        var job = new PairJob<HandwrittenSpatialPartitions>(maintenance.Query, pairs, parallel);
        ReadOnlySpan<JobResourceAccess> output = [pairs.FillAccess];
        return maintenance.ScheduleRead(in job, output, dependency);
    }

    public static JobHandle ScheduleConsume(HandwrittenSpatialMaintenance maintenance, CollisionPairBuffer pairs,
        SpatialContactTotals totals, JobHandle dependency = default)
    {
        var job = new ConsumeJob(maintenance.World, maintenance.Bodies, pairs, totals);
        ReadOnlySpan<JobResourceAccess> output = [pairs.ReadAccess, totals.WriteAccess];
        return maintenance.ScheduleRead(in job, output, dependency);
    }

    public static JobHandle ScheduleParallel(HandwrittenSpatialMaintenance maintenance, CollisionPairBuffer pairs,
        SpatialContactTotals totals, JobHandle dependency = default) =>
        ScheduleConsume(maintenance, pairs, totals, SchedulePairsParallel(maintenance, pairs, dependency));

    private readonly struct PairJob<TEnumerable>(RelationQuery<TEnumerable, (Entity, Entity)> query,
        CollisionPairBuffer pairs, bool parallel) : IJob
        where TEnumerable : struct, ICollisionPartitionEnumerable
    {
        public void Execute()
        {
            if (parallel)
                query.FillPairsParallel(pairs);
            else
                query.FillPairs(pairs);
        }
    }

    private readonly struct ConsumeJob(World world, QueryHandle bodies, CollisionPairBuffer pairs,
        SpatialContactTotals totals) : IJob
    {
        public void Execute() => totals.Consume(world, bodies, pairs.Pairs);
    }
}
