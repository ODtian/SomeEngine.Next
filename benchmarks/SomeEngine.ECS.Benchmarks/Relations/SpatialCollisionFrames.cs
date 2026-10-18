using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Relations;
using SomeEngine.Job;
using SomeEngine.Physics;

namespace SomeEngine.ECS.Benchmarks.Relations.Spatial;

/// <summary>Actual frame calls used by both the maintained-query example and paired measurements.</summary>
public static class SpatialCollisionFrames
{
    public static JobHandle Schedule<TGeometry, TBoundsReader, TFilter, TGroupResults>(
        CollisionMaintenance<TGeometry, TBoundsReader, TFilter, TGroupResults> maintenance,
        SpatialContactBuffer contacts, JobHandle dependency = default)
        where TGeometry : struct
        where TBoundsReader : struct, ICollisionBoundsReader<TGeometry>
        where TFilter : struct, ISpatialFilter<TGroupResults>
        where TGroupResults : unmanaged
    {
        var job = new ContactJob<TGeometry, TBoundsReader, TFilter, TGroupResults>(maintenance.Query, contacts);
        return maintenance.ScheduleRead(in job, contacts.Writes, dependency);
    }

    public static JobHandle Schedule(HandwrittenSpatialMaintenance maintenance, SpatialContactBuffer contacts,
        JobHandle dependency = default)
    {
        var job = new HandwrittenContactJob(maintenance, contacts);
        return maintenance.ScheduleRead(in job, contacts.Writes, dependency);
    }

    private readonly struct ContactJob<TGeometry, TBoundsReader, TFilter, TGroupResults>(
        RelationQuery<SpatialCandidates<TGeometry, TBoundsReader, TFilter, TGroupResults>, (Entity, Entity)> query,
        SpatialContactBuffer contacts) : IJob
        where TGeometry : struct
        where TBoundsReader : struct, ICollisionBoundsReader<TGeometry>
        where TFilter : struct, ISpatialFilter<TGroupResults>
        where TGroupResults : unmanaged
    {
        public void Execute()
        {
            var output = new SpatialPairOutput(contacts.PairStorage);
            query.Enumerate(ref output);
            contacts.Consume(query.Enumerable.World, query.Enumerable.GetQuery(0), output.Count);
        }
    }

    private readonly struct HandwrittenContactJob(HandwrittenSpatialMaintenance maintenance,
        SpatialContactBuffer contacts) : IJob
    {
        public void Execute()
        {
            var output = new SpatialPairOutput(contacts.PairStorage);
            maintenance.Enumerate(ref output);
            contacts.Consume(maintenance.World, maintenance.Bodies, output.Count);
        }
    }
}
