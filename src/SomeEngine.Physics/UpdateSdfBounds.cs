using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics;

public readonly struct UpdateSdfBounds<TField>(int rowsPerPacket) : IPhysicsStage where TField : struct, ISdfField<TField>
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) =>
        new UpdateSdfBoundsJob<TField>().ScheduleParallel(context.World,
            new JobEntityScheduleOptions(rowsPerPacket: rowsPerPacket, filter: context.Bodies), dependency);
}

internal readonly partial struct UpdateSdfBoundsJob<TField> : IJobEntity where TField : struct, ISdfField<TField>
{
    public void Execute([JobResources(typeof(SdfGeometryResources<>))] in SdfGeometry<TField> geometry,
        in BodyPose pose, ref CollisionBounds bounds) => bounds = SdfGeometry<TField>.Bounds(in geometry, in pose);
}
