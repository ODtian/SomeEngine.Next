using SomeEngine.ECS;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics;

/// <summary>Updates derived collision ranges from the original geometry and current body-pose columns.</summary>
public readonly struct UpdateGeometryBounds<TGeometry>(int rowsPerPacket) : IPhysicsStage
    where TGeometry : unmanaged, IComponent, ICollisionGeometry<TGeometry>
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) =>
        new SystemRunner(context.World, rowsPerPacket).Schedule(context.Bodies,
            new UpdateGeometryBoundsJob<TGeometry>(), dependency, parallel: true);
}

internal readonly partial struct UpdateGeometryBoundsJob<TGeometry> : IJobEntity
    where TGeometry : unmanaged, IComponent, ICollisionGeometry<TGeometry>
{
    public void Execute(in TGeometry geometry, in BodyPose pose, ref CollisionBounds bounds) =>
        bounds = TGeometry.Bounds(in geometry, in pose);
}
