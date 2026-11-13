using SomeEngine.ECS;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics;

/// <summary>Writes the common start/prediction envelope and angular travel into the existing bounds component.</summary>
public readonly struct UpdatePredictedBounds<TGeometry>(float padding, int rowsPerPacket) : IPhysicsStage
    where TGeometry : unmanaged, IComponent, ICollisionGeometry<TGeometry>
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) =>
        new SystemRunner(context.World, rowsPerPacket).Schedule(context.Bodies,
            new UpdatePredictedBoundsJob<TGeometry>((float)context.Duration, padding), dependency, parallel: true);
}

internal readonly partial struct UpdatePredictedBoundsJob<TGeometry>(float duration, float padding) : IJobEntity
    where TGeometry : unmanaged, IComponent, ICollisionGeometry<TGeometry>
{
    public void Execute(in TGeometry geometry, in BodyPose pose, in BodyMass mass, in BodyStepState step,
        in BodyVelocity velocity, ref CollisionBounds bounds) =>
        bounds = BodyStepGeometry.Bounds(in geometry, in pose, in mass, in step, in velocity, duration, padding);
}
