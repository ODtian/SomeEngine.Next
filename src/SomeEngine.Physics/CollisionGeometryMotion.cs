using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics;

/// <summary>A concrete geometry reader that also maps the common substep's starting geometry and motion range.</summary>
public interface ICollisionGeometryMotion<TGeometry> : ICollisionGeometryReader<TGeometry> where TGeometry : struct
{
    TGeometry ReadStartGeometry(World world, Entity entity, out BodyPose pose);
    void GetStartBodies(World world, Entity entity, Vector3 point, Span<ContactBody> bodies);
    CollisionBounds ReadMotionBounds(World world, Entity entity, in PhysicsStepContext context, float padding);
}

public readonly struct ComponentGeometryMotion<TGeometry>(ContactNormalReference normalReference = ContactNormalReference.MaterialPoint)
    : ICollisionGeometryMotion<TGeometry>
    where TGeometry : struct, ICollisionGeometry<TGeometry>
{
    public static bool HasGeometry(World world, Entity entity) => world.Has<TGeometry>(entity);
    public QueryDescription AddReads(QueryDescription definition) =>
        new ComponentGeometry<TGeometry>().AddReads(definition).Optional<BodyMass>()
            .Optional<BodyStepState>().Optional<BodyVelocity>();
    public void AddInputAccesses(World world, List<JobResourceAccess> accesses) => new ComponentGeometry<TGeometry>().AddInputAccesses(world, accesses);
    public void AddAccesses(World world, List<JobResourceAccess> accesses)
    {
        new ComponentGeometry<TGeometry>().AddAccesses(world, accesses);
        var jobs = (WorldJobs)world.Execution!;
        accesses.Add(jobs.Read<BodyMass>()); accesses.Add(jobs.Read<BodyStepState>()); accesses.Add(jobs.Read<BodyVelocity>());
    }
    public TGeometry ReadGeometry(World world, Entity entity, out BodyPose pose) =>
        new ComponentGeometry<TGeometry>().ReadGeometry(world, entity, out pose);
    public TGeometry ReadStartGeometry(World world, Entity entity, out BodyPose pose)
    {
        pose = BodyStepGeometry.Start(in world.Read<BodyMass>(entity), in world.Read<BodyStepState>(entity));
        return world.Read<TGeometry>(entity);
    }
    public int GetBodyCount(World world, Entity entity) => 1;
    public void GetBodies(World world, Entity entity, Vector3 point, Span<ContactBody> bodies) =>
        new ComponentGeometry<TGeometry>(normalReference).GetBodies(world, entity, point, bodies);
    public void GetStartBodies(World world, Entity entity, Vector3 point, Span<ContactBody> bodies)
    {
        BodyPose start = BodyStepGeometry.Start(in world.Read<BodyMass>(entity), in world.Read<BodyStepState>(entity));
        bodies[0] = new(entity, GeometrySpace.ToLocal(in start, point), 1, normalReference);
    }
    public CollisionBounds ReadMotionBounds(World world, Entity entity, in PhysicsStepContext context, float padding) =>
        BodyStepGeometry.Bounds(in world.Read<TGeometry>(entity), in world.Read<BodyPose>(entity), in world.Read<BodyMass>(entity),
            in world.Read<BodyStepState>(entity), in world.Read<BodyVelocity>(entity), (float)context.Duration, padding);
}

internal static class BodyStepGeometry
{
    internal static BodyPose Start(in BodyMass mass, in BodyStepState step)
    {
        var pose = new BodyPose { Rotation = step.StartRotation };
        pose.SetCenterOfMass(step.StartCenterOfMass, in mass);
        return pose;
    }
    internal static BodyPose Predicted(in BodyMass mass, in BodyStepState step)
    {
        var pose = new BodyPose { Rotation = step.PredictedRotation };
        pose.SetCenterOfMass(step.PredictedCenterOfMass, in mass);
        return pose;
    }
    internal static CollisionBounds Bounds<TGeometry>(in TGeometry geometry, in BodyPose current, in BodyMass mass,
        in BodyStepState step, in BodyVelocity velocity, float duration, float padding)
        where TGeometry : struct, ICollisionGeometry<TGeometry>
    {
        BodyPose start = Start(in mass, in step), predicted = Predicted(in mass, in step);
        CollisionBounds first = TGeometry.Bounds(in geometry, in start);
        CollisionBounds last = TGeometry.Bounds(in geometry, in predicted);
        CollisionBounds now = TGeometry.Bounds(in geometry, in current);
        CollisionBounds combined = CollisionBounds.Union(in first, in last);
        combined = CollisionBounds.Union(in combined, in now);
        float angularTravel = mass.Motion == BodyMotion.Static ? 0 : velocity.Angular.Length() * duration;
        if (angularTravel > 0)
        {
            Vector3 farthest = Vector3.Max(Vector3.Abs(first.Min - step.StartCenterOfMass), Vector3.Abs(first.Max - step.StartCenterOfMass));
            padding += farthest.Length() * angularTravel;
        }
        var amount = new Vector3(padding);
        return new(combined.Min - amount, combined.Max + amount);
    }
    internal static Vector3 Delta(ReadOnlySpan<ContactBody> first, ReadOnlySpan<ContactBody> second,
        World world, bool predicted, bool normal = false)
    {
        Vector3 delta = default;
        foreach (ref readonly ContactBody body in first) delta -= Point(in body, world, predicted, normal) * body.Weight;
        foreach (ref readonly ContactBody body in second) delta += Point(in body, world, predicted, normal) * body.Weight;
        return delta;
    }
    private static Vector3 Point(in ContactBody body, World world, bool predicted, bool normal)
    {
        ref readonly BodyMass mass = ref world.Read<BodyMass>(body.Body);
        ref readonly BodyStepState step = ref world.Read<BodyStepState>(body.Body);
        Quaternion rotation = predicted ? step.PredictedRotation : step.StartRotation;
        Vector3 center = predicted ? step.PredictedCenterOfMass : step.StartCenterOfMass;
        return center + Vector3.Transform((normal ? body.NormalLocalPoint : body.LocalPoint) - mass.CenterOfMass, rotation);
    }
}
