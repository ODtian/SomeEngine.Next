using System.Numerics;
using SomeEngine.ECS.Queries;

namespace SomeEngine.Physics;

/// <summary>A concrete geometry reader that also maps the common substep's starting geometry and motion range.</summary>
public interface ICollisionGeometryMotion<TGeometry> : ICollisionGeometryReader<TGeometry> where TGeometry : struct
{
    TGeometry ReadStartGeometry(scoped QueryRow row, scoped QueryCursor access, out BodyPose pose);
    void GetStartBodies(scoped QueryRow row, scoped QueryCursor access, Vector3 point, Span<ContactBody> bodies);
    CollisionBounds ReadMotionBounds(scoped QueryRow row, scoped QueryCursor access, in PhysicsStepContext context, float padding);
}

public readonly struct ComponentGeometryMotion<TGeometry>(ContactNormalReference normalReference = ContactNormalReference.MaterialPoint)
    : ICollisionGeometryMotion<TGeometry>
    where TGeometry : struct, ICollisionGeometry<TGeometry>
{
    public QueryDefinition AddReads(QueryDefinition definition) =>
        new ComponentGeometry<TGeometry>().AddReads(definition).WithOptionalRead<BodyMass>()
            .WithOptionalRead<BodyStepState>().WithOptionalRead<BodyVelocity>();
    public TGeometry ReadGeometry(scoped QueryRow row, scoped QueryCursor access, out BodyPose pose) =>
        new ComponentGeometry<TGeometry>().ReadGeometry(row, access, out pose);
    public TGeometry ReadStartGeometry(scoped QueryRow row, scoped QueryCursor access, out BodyPose pose)
    {
        pose = BodyStepGeometry.Start(in row.ReadRef<BodyMass>(), in row.ReadRef<BodyStepState>());
        return row.Read<TGeometry>();
    }
    public int GetBodyCount(scoped QueryRow row, scoped QueryCursor access) => 1;
    public void GetBodies(scoped QueryRow row, scoped QueryCursor access, Vector3 point, Span<ContactBody> bodies) =>
        new ComponentGeometry<TGeometry>(normalReference).GetBodies(row, access, point, bodies);
    public void GetStartBodies(scoped QueryRow row, scoped QueryCursor access, Vector3 point, Span<ContactBody> bodies)
    {
        BodyPose start = BodyStepGeometry.Start(in row.ReadRef<BodyMass>(), in row.ReadRef<BodyStepState>());
        bodies[0] = new(row.Entity, GeometrySpace.ToLocal(in start, point), 1, normalReference);
    }
    public CollisionBounds ReadMotionBounds(scoped QueryRow row, scoped QueryCursor access, in PhysicsStepContext context, float padding) =>
        BodyStepGeometry.Bounds(in row.ReadRef<TGeometry>(), in row.ReadRef<BodyPose>(), in row.ReadRef<BodyMass>(),
            in row.ReadRef<BodyStepState>(), in row.ReadRef<BodyVelocity>(), (float)context.Duration, padding);
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
        scoped QueryCursor access, bool predicted, bool normal = false)
    {
        Vector3 delta = default;
        foreach (ref readonly ContactBody body in first) delta -= Point(in body, access, predicted, normal) * body.Weight;
        foreach (ref readonly ContactBody body in second) delta += Point(in body, access, predicted, normal) * body.Weight;
        return delta;
    }
    private static Vector3 Point(in ContactBody body, scoped QueryCursor access, bool predicted, bool normal)
    {
        access.TryGetRow(body.Body, out QueryRow row);
        ref readonly BodyMass mass = ref row.ReadRef<BodyMass>();
        ref readonly BodyStepState step = ref row.ReadRef<BodyStepState>();
        Quaternion rotation = predicted ? step.PredictedRotation : step.StartRotation;
        Vector3 center = predicted ? step.PredictedCenterOfMass : step.StartCenterOfMass;
        return center + Vector3.Transform((normal ? body.NormalLocalPoint : body.LocalPoint) - mass.CenterOfMass, rotation);
    }
}
