using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;

namespace SomeEngine.Physics;

/// <summary>A position sample attached to an actual simulated body.</summary>
public readonly record struct GeometryEndpoint(Entity Body, Vector3 LocalPoint);

public readonly record struct DynamicCapsule(GeometryEndpoint First, GeometryEndpoint Second, float Radius) : IComponent;

public readonly struct DynamicCapsuleReader : ICollisionGeometryReader<CapsuleGeometry>
{
    public static bool HasGeometry(scoped QueryRow row) => row.Has<DynamicCapsule>();

    public QueryDefinition AddReads(QueryDefinition definition) =>
        definition.WithOptionalRead<DynamicCapsule>().WithOptionalRead<BodyPose>();

    public CapsuleGeometry ReadGeometry(scoped QueryRow row, scoped QueryCursor access, out BodyPose pose)
    {
        ref readonly DynamicCapsule capsule = ref row.ReadRef<DynamicCapsule>();
        return GeometryEndpoints.ReadCapsule(capsule.First, capsule.Second, capsule.Radius, access, out pose);
    }

    public int GetBodyCount(scoped QueryRow row, scoped QueryCursor access) => 2;

    public void GetBodies(scoped QueryRow row, scoped QueryCursor access, Vector3 point, Span<ContactBody> bodies)
    {
        ref readonly DynamicCapsule capsule = ref row.ReadRef<DynamicCapsule>();
        GeometryEndpoints.WriteSegmentBodies(capsule.First, capsule.Second, access, point, bodies);
    }
}

internal static class GeometryEndpoints
{
    internal static Vector3 ReadPoint(in GeometryEndpoint endpoint, scoped QueryCursor access)
    {
        access.TryGetRow(endpoint.Body, out QueryRow body);
        return GeometrySpace.ToWorld(in body.ReadRef<BodyPose>(), endpoint.LocalPoint);
    }

    internal static CapsuleGeometry ReadCapsule(in GeometryEndpoint first, in GeometryEndpoint second, float radius,
        scoped QueryCursor access, out BodyPose pose)
    {
        Vector3 start = ReadPoint(in first, access), end = ReadPoint(in second, access);
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
        scoped QueryCursor access, Vector3 point, Span<ContactBody> bodies)
    {
        Vector3 start = ReadPoint(in first, access), end = ReadPoint(in second, access);
        Vector3 edge = end - start;
        float parameter = Math.Clamp(Vector3.Dot(point - start, edge) / edge.LengthSquared(), 0, 1);
        bodies[0] = new(first.Body, first.LocalPoint, 1 - parameter);
        bodies[1] = new(second.Body, second.LocalPoint, parameter);
    }
}
