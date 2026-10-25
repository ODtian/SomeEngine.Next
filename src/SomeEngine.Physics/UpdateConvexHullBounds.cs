using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics;

/// <summary>Reads the shared hull arrays after dependencies and updates each entity's derived bounds.</summary>
public readonly struct UpdateConvexHullBounds(int rowsPerPacket) : IPhysicsStage
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) =>
        new UpdateConvexHullBoundsJob().ScheduleParallel(context.World,
            new JobEntityScheduleOptions(rowsPerPacket: rowsPerPacket, filter: context.Bodies), dependency);
}

public readonly struct ConvexHullResources : IJobComponentResources<ConvexHullGeometry>
{
    public static void Collect(in ConvexHullGeometry component, ref JobResourceAccessBuilder resources)
    {
        resources.Read(component.Data.Vertices);
        resources.Read(component.Data.Faces);
        resources.Read(component.Data.FaceVertices);
    }
}

internal readonly partial struct UpdateConvexHullBoundsJob : IJobEntity
{
    public void Execute([JobResources<ConvexHullResources>] in ConvexHullGeometry geometry,
        in BodyPose pose, ref CollisionBounds bounds) => bounds = ConvexHullGeometry.Bounds(in geometry, in pose);
}
