using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;

namespace SomeEngine.Physics;

/// <summary>A continuous, zero-radius segment between two current body samples.</summary>
public readonly record struct DynamicEdge(GeometryEndpoint First, GeometryEndpoint Second) : ITrackedComponent;

public readonly struct DynamicEdgeReader : ICollisionGeometryMotion<CapsuleGeometry>
{
    public static bool HasGeometry(World world, Entity entity) => world.Has<DynamicEdge>(entity);

    public QueryDescription AddReads(QueryDescription definition) =>
        GeometryEndpoints.AddReads(definition).Optional<DynamicEdge>();

    public CapsuleGeometry ReadGeometry(World world, Entity entity, out BodyPose pose)
    {
        ref readonly DynamicEdge edge = ref world.Read<DynamicEdge>(entity);
        return GeometryEndpoints.ReadCapsule(edge.First, edge.Second, 0, world, out pose);
    }

    public CapsuleGeometry ReadStartGeometry(World world, Entity entity, out BodyPose pose)
    {
        ref readonly DynamicEdge edge = ref world.Read<DynamicEdge>(entity);
        return GeometryEndpoints.ReadCapsule(edge.First, edge.Second, 0, world, out pose, GeometryPointTime.Start);
    }

    public void GetStartBodies(World world, Entity entity, Vector3 point, Span<ContactBody> bodies)
    {
        ref readonly DynamicEdge edge = ref world.Read<DynamicEdge>(entity);
        GeometryEndpoints.WriteSegmentBodies(edge.First, edge.Second, world, point, bodies, GeometryPointTime.Start);
    }

    public CollisionBounds ReadMotionBounds(World world, Entity entity, in PhysicsStepContext context, float padding)
    {
        ref readonly DynamicEdge edge = ref world.Read<DynamicEdge>(entity);
        CollisionBounds first = GeometryEndpoints.ReadMotionBounds(edge.First, world, in context, padding);
        CollisionBounds second = GeometryEndpoints.ReadMotionBounds(edge.Second, world, in context, padding);
        return CollisionBounds.Union(in first, in second);
    }

    public int GetBodyCount(World world, Entity entity) => 2;

    public void GetBodies(World world, Entity entity, Vector3 point, Span<ContactBody> bodies)
    {
        ref readonly DynamicEdge edge = ref world.Read<DynamicEdge>(entity);
        GeometryEndpoints.WriteSegmentBodies(edge.First, edge.Second, world, point, bodies);
    }
}
