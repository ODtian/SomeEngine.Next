using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics;

/// <summary>Updates selected bounds from a concrete reader and its declared geometry inputs.</summary>
public readonly struct UpdateGeometryBounds<TGeometry, TReader>(World world, Query members, TReader reader, int rowsPerPacket) : IPhysicsStage
    where TGeometry : struct, ICollisionGeometry<TGeometry>
    where TReader : struct, ICollisionGeometryReader<TGeometry>
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) =>
        CollisionBoundsJobs.Schedule(world, members, new ReadBounds(reader), rowsPerPacket, in context, dependency);

    private readonly struct ReadBounds(TReader reader) : ICollisionBoundsWork
    {
        public void AddInputAccesses(World world, List<JobResourceAccess> accesses) => reader.AddInputAccesses(world, accesses);
        public void AddAccesses(World world, List<JobResourceAccess> accesses) => reader.AddAccesses(world, accesses);
        public CollisionBounds Read(World world, Entity entity, in PhysicsStepContext context)
        {
            TGeometry geometry = reader.ReadGeometry(world, entity, out BodyPose pose);
            return TGeometry.Bounds(in geometry, in pose);
        }
    }
}

internal interface ICollisionBoundsWork
{
    void AddInputAccesses(World world, List<JobResourceAccess> accesses);
    void AddAccesses(World world, List<JobResourceAccess> accesses);
    CollisionBounds Read(World world, Entity entity, in PhysicsStepContext context);
}

internal static class CollisionBoundsJobs
{
    internal static JobHandle Schedule<TWork>(World world, Query members, TWork work, int rowsPerRange,
        in PhysicsStepContext context, JobHandle dependency) where TWork : struct, ICollisionBoundsWork =>
        world.Execution!.Scheduler!.Schedule(new Capture<TWork>(world, members, work, rowsPerRange, context),
            ((WorldJobs)world.Execution!).StructureRead, dependency: dependency);

    private readonly struct Capture<TWork>(World world, Query members, TWork work, int rowsPerRange, PhysicsStepContext context) : IJob
        where TWork : struct, ICollisionBoundsWork
    {
        public void Execute()
        {
            var accesses = new List<JobResourceAccess>();
            work.AddInputAccesses(world, accesses);
            if (accesses.Count == 0) ScheduleWrite(world, members, work, rowsPerRange, in context);
            else
            {
                accesses.AddRange(((WorldJobs)world.Execution!).StructureRead);
                world.Execution!.Scheduler!.Schedule(new CaptureInputs<TWork>(world, members, work, rowsPerRange, context), accesses.ToArray());
            }
        }
    }

    private readonly struct CaptureInputs<TWork>(World world, Query members, TWork work, int rowsPerRange, PhysicsStepContext context) : IJob
        where TWork : struct, ICollisionBoundsWork
    {
        public void Execute() => ScheduleWrite(world, members, work, rowsPerRange, in context);
    }

    private static void ScheduleWrite<TWork>(World world, Query members, TWork work, int rowsPerRange,
        in PhysicsStepContext context) where TWork : struct, ICollisionBoundsWork
    {
        var accesses = new List<JobResourceAccess>();
        work.AddAccesses(world, accesses);
        new SystemRunner(world, rowsPerRange).Schedule(members, new WriteBounds<TWork>(world, work, context, accesses.ToArray()));
    }

    private readonly struct WriteBounds<TWork>(World world, TWork work, PhysicsStepContext context,
        JobResourceAccess[] inputs) : IChunkAction where TWork : struct, ICollisionBoundsWork
    {
        private static readonly ChunkAccess[] Fields = [ChunkAccess.Write<CollisionBounds>()];
        public static ReadOnlySpan<ChunkAccess> Components => Fields;
        public void CollectAdditionalResources(ref JobResourceAccessBuilder resources)
        {
            foreach (JobResourceAccess access in inputs) resources.Add(access);
        }
        public void Execute(SystemChunk rows)
        {
            for (int row = 0; row < rows.Count; row++)
                rows.Write<CollisionBounds>(row) = work.Read(world, rows.Entities[row], in context);
        }
    }
}
