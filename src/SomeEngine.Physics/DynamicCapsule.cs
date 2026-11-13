using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;

namespace SomeEngine.Physics;

/// <summary>A position sample attached to an actual simulated body.</summary>
public readonly record struct GeometryEndpoint(Entity Body, Vector3 LocalPoint);

public readonly record struct DynamicCapsule(GeometryEndpoint First, GeometryEndpoint Second, float Radius) : ITrackedComponent;

public readonly struct DynamicCapsuleReader : ICollisionGeometryMotion<CapsuleGeometry>
{
    public static bool HasGeometry(World world, Entity entity) => world.Has<DynamicCapsule>(entity);

    public QueryDescription AddReads(QueryDescription definition) =>
        GeometryEndpoints.AddReads(definition).Optional<DynamicCapsule>();

    public CapsuleGeometry ReadGeometry(World world, Entity entity, out BodyPose pose)
    {
        ref readonly DynamicCapsule capsule = ref world.Read<DynamicCapsule>(entity);
        return GeometryEndpoints.ReadCapsule(capsule.First, capsule.Second, capsule.Radius, world, out pose);
    }

    public CapsuleGeometry ReadStartGeometry(World world, Entity entity, out BodyPose pose)
    {
        ref readonly DynamicCapsule capsule = ref world.Read<DynamicCapsule>(entity);
        return GeometryEndpoints.ReadCapsule(capsule.First, capsule.Second, capsule.Radius, world, out pose, GeometryPointTime.Start);
    }

    public void GetStartBodies(World world, Entity entity, Vector3 point, Span<ContactBody> bodies)
    {
        ref readonly DynamicCapsule capsule = ref world.Read<DynamicCapsule>(entity);
        GeometryEndpoints.WriteSegmentBodies(capsule.First, capsule.Second, world, point, bodies, GeometryPointTime.Start);
    }

    public CollisionBounds ReadMotionBounds(World world, Entity entity, in PhysicsStepContext context, float padding)
    {
        ref readonly DynamicCapsule capsule = ref world.Read<DynamicCapsule>(entity);
        CollisionBounds first = GeometryEndpoints.ReadMotionBounds(capsule.First, world, in context, capsule.Radius + padding);
        CollisionBounds second = GeometryEndpoints.ReadMotionBounds(capsule.Second, world, in context, capsule.Radius + padding);
        return CollisionBounds.Union(in first, in second);
    }

    public int GetBodyCount(World world, Entity entity) => 2;

    public void GetBodies(World world, Entity entity, Vector3 point, Span<ContactBody> bodies)
    {
        ref readonly DynamicCapsule capsule = ref world.Read<DynamicCapsule>(entity);
        GeometryEndpoints.WriteSegmentBodies(capsule.First, capsule.Second, world, point, bodies);
    }
}

internal enum GeometryPointTime { Current, Start, Predicted }

internal static class GeometryEndpoints
{
    internal static QueryDescription AddReads(QueryDescription definition) => definition.Optional<BodyPose>()
        .Optional<BodyMass>().Optional<BodyStepState>().Optional<BodyVelocity>();

    internal static BodyPose ReadPose(in GeometryEndpoint endpoint, World world, GeometryPointTime time)
    {
        return time switch
        {
            GeometryPointTime.Start => BodyStepGeometry.Start(in world.Read<BodyMass>(endpoint.Body), in world.Read<BodyStepState>(endpoint.Body)),
            GeometryPointTime.Predicted => BodyStepGeometry.Predicted(in world.Read<BodyMass>(endpoint.Body), in world.Read<BodyStepState>(endpoint.Body)),
            _ => world.Read<BodyPose>(endpoint.Body),
        };
    }

    internal static Vector3 ReadPoint(in GeometryEndpoint endpoint, World world, GeometryPointTime time = GeometryPointTime.Current)
    {
        BodyPose pose = ReadPose(in endpoint, world, time);
        return GeometrySpace.ToWorld(in pose, endpoint.LocalPoint);
    }

    internal static CollisionBounds ReadMotionBounds(in GeometryEndpoint endpoint, World world,
        in PhysicsStepContext context, float radius)
    {
        Vector3 current = ReadPoint(in endpoint, world), start = ReadPoint(in endpoint, world, GeometryPointTime.Start),
            predicted = ReadPoint(in endpoint, world, GeometryPointTime.Predicted);
        ref readonly BodyMass mass = ref world.Read<BodyMass>(endpoint.Body);
        float travel = mass.Motion == BodyMotion.Static ? 0 : world.Read<BodyVelocity>(endpoint.Body).Angular.Length() * (float)context.Duration;
        radius += (endpoint.LocalPoint - mass.CenterOfMass).Length() * MathF.Min(travel, 2);
        return new(Vector3.Min(current, Vector3.Min(start, predicted)) - new Vector3(radius),
            Vector3.Max(current, Vector3.Max(start, predicted)) + new Vector3(radius));
    }

    internal static CapsuleGeometry ReadCapsule(in GeometryEndpoint first, in GeometryEndpoint second, float radius,
        World world, out BodyPose pose, GeometryPointTime time = GeometryPointTime.Current)
    {
        Vector3 start = ReadPoint(in first, world, time), end = ReadPoint(in second, world, time);
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
        World world, Vector3 point, Span<ContactBody> bodies, GeometryPointTime time = GeometryPointTime.Current)
    {
        Vector3 start = ReadPoint(in first, world, time), end = ReadPoint(in second, world, time);
        Vector3 edge = end - start;
        float parameter = Math.Clamp(Vector3.Dot(point - start, edge) / edge.LengthSquared(), 0, 1);
        bodies[0] = new(first.Body, first.LocalPoint, 1 - parameter);
        bodies[1] = new(second.Body, second.LocalPoint, parameter);
    }
}
