using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Queries;

namespace SomeEngine.Physics;

public readonly record struct DynamicFace(GeometryEndpoint First, GeometryEndpoint Second, GeometryEndpoint Third,
    float Radius) : IComponent;

public readonly struct DynamicFaceReader : ICollisionGeometryReader<TriangleGeometry>
{
    public static bool HasGeometry(scoped QueryRow row) => row.Has<DynamicFace>();

    public QueryDefinition AddReads(QueryDefinition definition) =>
        definition.WithOptionalRead<DynamicFace>().WithOptionalRead<BodyPose>();

    public TriangleGeometry ReadGeometry(scoped QueryRow row, scoped QueryCursor access, out BodyPose pose)
    {
        ref readonly DynamicFace face = ref row.ReadRef<DynamicFace>();
        access.TryGetRow(face.First.Body, out QueryRow first);
        pose = first.Read<BodyPose>();
        return new(face.First.LocalPoint,
            GeometrySpace.ToLocal(in pose, GeometryEndpoints.ReadPoint(face.Second, access)),
            GeometrySpace.ToLocal(in pose, GeometryEndpoints.ReadPoint(face.Third, access)), face.Radius);
    }

    public int GetBodyCount(scoped QueryRow row, scoped QueryCursor access) => 3;

    public void GetBodies(scoped QueryRow row, scoped QueryCursor access, Vector3 point, Span<ContactBody> bodies)
    {
        ref readonly DynamicFace face = ref row.ReadRef<DynamicFace>();
        Vector3 first = GeometryEndpoints.ReadPoint(face.First, access);
        Vector3 second = GeometryEndpoints.ReadPoint(face.Second, access);
        Vector3 third = GeometryEndpoints.ReadPoint(face.Third, access);
        Vector3 closest = CollisionTriangles.ClosestPoint(point, first, second, third);
        Vector3 weights = CollisionTriangles.GetWeights(closest, first, second, third);
        bodies[0] = new(face.First.Body, face.First.LocalPoint, weights.X);
        bodies[1] = new(face.Second.Body, face.Second.LocalPoint, weights.Y);
        bodies[2] = new(face.Third.Body, face.Third.LocalPoint, weights.Z);
    }
}
