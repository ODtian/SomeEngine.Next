using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;

namespace SomeEngine.Physics;

public readonly record struct DynamicFace(GeometryEndpoint First, GeometryEndpoint Second, GeometryEndpoint Third,
    float Radius) : ITrackedComponent;

public readonly struct DynamicFaceReader : ICollisionGeometryMotion<TriangleGeometry>
{
    public static bool HasGeometry(World world, Entity entity) => world.Has<DynamicFace>(entity);

    public QueryDescription AddReads(QueryDescription definition) =>
        GeometryEndpoints.AddReads(definition).Optional<DynamicFace>();

    public TriangleGeometry ReadGeometry(World world, Entity entity, out BodyPose pose)
    {
        ref readonly DynamicFace face = ref world.Read<DynamicFace>(entity);
        return ReadFace(in face, world, out pose, GeometryPointTime.Current);
    }

    public TriangleGeometry ReadStartGeometry(World world, Entity entity, out BodyPose pose) =>
        ReadFace(in world.Read<DynamicFace>(entity), world, out pose, GeometryPointTime.Start);

    private static TriangleGeometry ReadFace(in DynamicFace face, World world, out BodyPose pose, GeometryPointTime time)
    {
        pose = GeometryEndpoints.ReadPose(face.First, world, time);
        return new(face.First.LocalPoint,
            GeometrySpace.ToLocal(in pose, GeometryEndpoints.ReadPoint(face.Second, world, time)),
            GeometrySpace.ToLocal(in pose, GeometryEndpoints.ReadPoint(face.Third, world, time)), face.Radius);
    }

    public CollisionBounds ReadMotionBounds(World world, Entity entity, in PhysicsStepContext context, float padding)
    {
        ref readonly DynamicFace face = ref world.Read<DynamicFace>(entity);
        CollisionBounds first = GeometryEndpoints.ReadMotionBounds(face.First, world, in context, face.Radius + padding);
        CollisionBounds second = GeometryEndpoints.ReadMotionBounds(face.Second, world, in context, face.Radius + padding);
        CollisionBounds third = GeometryEndpoints.ReadMotionBounds(face.Third, world, in context, face.Radius + padding);
        CollisionBounds bounds = CollisionBounds.Union(in first, in second);
        return CollisionBounds.Union(in bounds, in third);
    }

    public int GetBodyCount(World world, Entity entity) => 3;

    public void GetBodies(World world, Entity entity, Vector3 point, Span<ContactBody> bodies)
        => WriteBodies(in world.Read<DynamicFace>(entity), world, point, bodies, GeometryPointTime.Current);

    public void GetStartBodies(World world, Entity entity, Vector3 point, Span<ContactBody> bodies)
        => WriteBodies(in world.Read<DynamicFace>(entity), world, point, bodies, GeometryPointTime.Start);

    private static void WriteBodies(in DynamicFace face, World world, Vector3 point, Span<ContactBody> bodies, GeometryPointTime time)
    {
        Vector3 first = GeometryEndpoints.ReadPoint(face.First, world, time);
        Vector3 second = GeometryEndpoints.ReadPoint(face.Second, world, time);
        Vector3 third = GeometryEndpoints.ReadPoint(face.Third, world, time);
        Vector3 closest = CollisionTriangles.ClosestPoint(point, first, second, third);
        Vector3 weights = CollisionTriangles.GetWeights(closest, first, second, third);
        bodies[0] = new(face.First.Body, face.First.LocalPoint, weights.X);
        bodies[1] = new(face.Second.Body, face.Second.LocalPoint, weights.Y);
        bodies[2] = new(face.Third.Body, face.Third.LocalPoint, weights.Z);
    }
}
