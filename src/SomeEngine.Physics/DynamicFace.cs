using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Queries;

namespace SomeEngine.Physics;

public readonly record struct DynamicFace(GeometryEndpoint First, GeometryEndpoint Second, GeometryEndpoint Third,
    float Radius) : IComponent;

public readonly struct DynamicFaceReader : ICollisionGeometryMotion<TriangleGeometry>
{
    public static bool HasGeometry(scoped QueryRow row) => row.Has<DynamicFace>();

    public QueryDefinition AddReads(QueryDefinition definition) =>
        GeometryEndpoints.AddReads(definition).WithOptionalRead<DynamicFace>();

    public TriangleGeometry ReadGeometry(scoped QueryRow row, scoped QueryCursor access, out BodyPose pose)
    {
        ref readonly DynamicFace face = ref row.ReadRef<DynamicFace>();
        return ReadFace(in face, access, out pose, GeometryPointTime.Current);
    }

    public TriangleGeometry ReadStartGeometry(scoped QueryRow row, scoped QueryCursor access, out BodyPose pose) =>
        ReadFace(in row.ReadRef<DynamicFace>(), access, out pose, GeometryPointTime.Start);

    private static TriangleGeometry ReadFace(in DynamicFace face, scoped QueryCursor access, out BodyPose pose, GeometryPointTime time)
    {
        pose = GeometryEndpoints.ReadPose(face.First, access, time);
        return new(face.First.LocalPoint,
            GeometrySpace.ToLocal(in pose, GeometryEndpoints.ReadPoint(face.Second, access, time)),
            GeometrySpace.ToLocal(in pose, GeometryEndpoints.ReadPoint(face.Third, access, time)), face.Radius);
    }

    public CollisionBounds ReadMotionBounds(scoped QueryRow row, scoped QueryCursor access, in PhysicsStepContext context, float padding)
    {
        ref readonly DynamicFace face = ref row.ReadRef<DynamicFace>();
        CollisionBounds first = GeometryEndpoints.ReadMotionBounds(face.First, access, in context, face.Radius + padding);
        CollisionBounds second = GeometryEndpoints.ReadMotionBounds(face.Second, access, in context, face.Radius + padding);
        CollisionBounds third = GeometryEndpoints.ReadMotionBounds(face.Third, access, in context, face.Radius + padding);
        CollisionBounds bounds = CollisionBounds.Union(in first, in second);
        return CollisionBounds.Union(in bounds, in third);
    }

    public int GetBodyCount(scoped QueryRow row, scoped QueryCursor access) => 3;

    public void GetBodies(scoped QueryRow row, scoped QueryCursor access, Vector3 point, Span<ContactBody> bodies)
        => WriteBodies(in row.ReadRef<DynamicFace>(), access, point, bodies, GeometryPointTime.Current);

    public void GetStartBodies(scoped QueryRow row, scoped QueryCursor access, Vector3 point, Span<ContactBody> bodies)
        => WriteBodies(in row.ReadRef<DynamicFace>(), access, point, bodies, GeometryPointTime.Start);

    private static void WriteBodies(in DynamicFace face, scoped QueryCursor access, Vector3 point, Span<ContactBody> bodies, GeometryPointTime time)
    {
        Vector3 first = GeometryEndpoints.ReadPoint(face.First, access, time);
        Vector3 second = GeometryEndpoints.ReadPoint(face.Second, access, time);
        Vector3 third = GeometryEndpoints.ReadPoint(face.Third, access, time);
        Vector3 closest = CollisionTriangles.ClosestPoint(point, first, second, third);
        Vector3 weights = CollisionTriangles.GetWeights(closest, first, second, third);
        bodies[0] = new(face.First.Body, face.First.LocalPoint, weights.X);
        bodies[1] = new(face.Second.Body, face.Second.LocalPoint, weights.Y);
        bodies[2] = new(face.Third.Body, face.Third.LocalPoint, weights.Z);
    }
}
