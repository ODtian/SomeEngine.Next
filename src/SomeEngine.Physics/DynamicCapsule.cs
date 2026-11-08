using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;

namespace SomeEngine.Physics;

/// <summary>A position sample attached to an actual simulated body.</summary>
public readonly record struct GeometryEndpoint(Entity Body, Vector3 LocalPoint);

public readonly record struct DynamicCapsule(GeometryEndpoint First, GeometryEndpoint Second, float Radius) : IComponent;

public readonly struct DynamicCapsuleReader : ICollisionGeometryMotion<CapsuleGeometry>
{
    public static bool HasGeometry(scoped QueryRow row) => row.Has<DynamicCapsule>();

    public QueryDefinition AddReads(QueryDefinition definition) =>
        GeometryEndpoints.AddReads(definition).WithOptionalRead<DynamicCapsule>();

    public CapsuleGeometry ReadGeometry(scoped QueryRow row, scoped QueryCursor access, out BodyPose pose)
    {
        ref readonly DynamicCapsule capsule = ref row.ReadRef<DynamicCapsule>();
        return GeometryEndpoints.ReadCapsule(capsule.First, capsule.Second, capsule.Radius, access, out pose);
    }

    public CapsuleGeometry ReadStartGeometry(scoped QueryRow row, scoped QueryCursor access, out BodyPose pose)
    {
        ref readonly DynamicCapsule capsule = ref row.ReadRef<DynamicCapsule>();
        return GeometryEndpoints.ReadCapsule(capsule.First, capsule.Second, capsule.Radius, access, out pose, GeometryPointTime.Start);
    }

    public void GetStartBodies(scoped QueryRow row, scoped QueryCursor access, Vector3 point, Span<ContactBody> bodies)
    {
        ref readonly DynamicCapsule capsule = ref row.ReadRef<DynamicCapsule>();
        GeometryEndpoints.WriteSegmentBodies(capsule.First, capsule.Second, access, point, bodies, GeometryPointTime.Start);
    }

    public CollisionBounds ReadMotionBounds(scoped QueryRow row, scoped QueryCursor access, in PhysicsStepContext context, float padding)
    {
        ref readonly DynamicCapsule capsule = ref row.ReadRef<DynamicCapsule>();
        CollisionBounds first = GeometryEndpoints.ReadMotionBounds(capsule.First, access, in context, capsule.Radius + padding);
        CollisionBounds second = GeometryEndpoints.ReadMotionBounds(capsule.Second, access, in context, capsule.Radius + padding);
        return CollisionBounds.Union(in first, in second);
    }

    public int GetBodyCount(scoped QueryRow row, scoped QueryCursor access) => 2;

    public void GetBodies(scoped QueryRow row, scoped QueryCursor access, Vector3 point, Span<ContactBody> bodies)
    {
        ref readonly DynamicCapsule capsule = ref row.ReadRef<DynamicCapsule>();
        GeometryEndpoints.WriteSegmentBodies(capsule.First, capsule.Second, access, point, bodies);
    }
}

internal enum GeometryPointTime { Current, Start, Predicted }

internal static class GeometryEndpoints
{
    internal static QueryDefinition AddReads(QueryDefinition definition) => definition.WithOptionalRead<BodyPose>()
        .WithOptionalRead<BodyMass>().WithOptionalRead<BodyStepState>().WithOptionalRead<BodyVelocity>();

    internal static BodyPose ReadPose(in GeometryEndpoint endpoint, scoped QueryCursor access, GeometryPointTime time)
    {
        access.TryGetRow(endpoint.Body, out QueryRow body);
        return time switch
        {
            GeometryPointTime.Start => BodyStepGeometry.Start(in body.ReadRef<BodyMass>(), in body.ReadRef<BodyStepState>()),
            GeometryPointTime.Predicted => BodyStepGeometry.Predicted(in body.ReadRef<BodyMass>(), in body.ReadRef<BodyStepState>()),
            _ => body.Read<BodyPose>(),
        };
    }

    internal static Vector3 ReadPoint(in GeometryEndpoint endpoint, scoped QueryCursor access, GeometryPointTime time = GeometryPointTime.Current)
    {
        BodyPose pose = ReadPose(in endpoint, access, time);
        return GeometrySpace.ToWorld(in pose, endpoint.LocalPoint);
    }

    internal static CollisionBounds ReadMotionBounds(in GeometryEndpoint endpoint, scoped QueryCursor access,
        in PhysicsStepContext context, float radius)
    {
        Vector3 current = ReadPoint(in endpoint, access), start = ReadPoint(in endpoint, access, GeometryPointTime.Start),
            predicted = ReadPoint(in endpoint, access, GeometryPointTime.Predicted);
        access.TryGetRow(endpoint.Body, out QueryRow body);
        ref readonly BodyMass mass = ref body.ReadRef<BodyMass>();
        float travel = mass.Motion == BodyMotion.Static ? 0 : body.ReadRef<BodyVelocity>().Angular.Length() * (float)context.Duration;
        radius += (endpoint.LocalPoint - mass.CenterOfMass).Length() * MathF.Min(travel, 2);
        return new(Vector3.Min(current, Vector3.Min(start, predicted)) - new Vector3(radius),
            Vector3.Max(current, Vector3.Max(start, predicted)) + new Vector3(radius));
    }

    internal static CapsuleGeometry ReadCapsule(in GeometryEndpoint first, in GeometryEndpoint second, float radius,
        scoped QueryCursor access, out BodyPose pose, GeometryPointTime time = GeometryPointTime.Current)
    {
        Vector3 start = ReadPoint(in first, access, time), end = ReadPoint(in second, access, time);
        Vector3 edge = end - start;
        float length = edge.Length();
        Vector3 y = edge / length;
        Vector3 x = Vector3.Normalize(Vector3.Cross(y, MathF.Abs(y.Z) < .9f ? Vector3.UnitZ : Vector3.UnitX));
        Vector3 z = Vector3.Cross(x, y);
        var frame = new Matrix4x4(x.X, x.Y, x.Z, 0, y.X, y.Y, y.Z, 0, z.X, z.Y, z.Z, 0, 0, 0, 0, 1);
        pose = new BodyPose { Position = (start + end) * .5f, Rotation = Quaternion.CreateFromRotationMatrix(frame) };
        return new(radius, length * .5f);
    }

    internal static void WriteSegmentBodies(in GeometryEndpoint first, in GeometryEndpoint second,
        scoped QueryCursor access, Vector3 point, Span<ContactBody> bodies, GeometryPointTime time = GeometryPointTime.Current)
    {
        Vector3 start = ReadPoint(in first, access, time), end = ReadPoint(in second, access, time);
        Vector3 edge = end - start;
        float parameter = Math.Clamp(Vector3.Dot(point - start, edge) / edge.LengthSquared(), 0, 1);
        bodies[0] = new(first.Body, first.LocalPoint, 1 - parameter);
        bodies[1] = new(second.Body, second.LocalPoint, parameter);
    }
}
