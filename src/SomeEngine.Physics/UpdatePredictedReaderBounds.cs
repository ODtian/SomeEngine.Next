using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.Job;

namespace SomeEngine.Physics;

/// <summary>Updates selected ranges from a concrete motion reader and its declared referenced inputs.</summary>
public readonly struct UpdatePredictedBounds<TGeometry, TMotion>(World world, Query members, TMotion motion,
    float padding, int rowsPerRange = 256) : IPhysicsStage
    where TGeometry : struct where TMotion : struct, ICollisionGeometryMotion<TGeometry>
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) =>
        CollisionBoundsJobs.Schedule(world, members, new ReadBounds(motion, padding), rowsPerRange, in context, dependency);

    private readonly struct ReadBounds(TMotion motion, float padding) : ICollisionBoundsWork
    {
        public void AddInputAccesses(World world, List<JobResourceAccess> accesses) => motion.AddInputAccesses(world, accesses);
        public void AddAccesses(World world, List<JobResourceAccess> accesses) => motion.AddAccesses(world, accesses);
        public CollisionBounds Read(World world, Entity entity, in PhysicsStepContext context) =>
            motion.ReadMotionBounds(world, entity, in context, padding);
    }
}
