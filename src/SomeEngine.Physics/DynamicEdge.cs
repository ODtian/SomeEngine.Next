using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Queries;

namespace SomeEngine.Physics;

/// <summary>A continuous, zero-radius segment between two current body samples.</summary>
public readonly record struct DynamicEdge(GeometryEndpoint First, GeometryEndpoint Second) : IComponent;

public readonly struct DynamicEdgeReader : ICollisionGeometryMotion<CapsuleGeometry>
{
    public static bool HasGeometry(scoped QueryRow row) => row.Has<DynamicEdge>();

    public QueryDefinition AddReads(QueryDefinition definition) =>
        GeometryEndpoints.AddReads(definition).WithOptionalRead<DynamicEdge>();

    public CapsuleGeometry ReadGeometry(scoped QueryRow row, scoped QueryCursor access, out BodyPose pose)
    {
        ref readonly DynamicEdge edge = ref row.ReadRef<DynamicEdge>();
        return GeometryEndpoints.ReadCapsule(edge.First, edge.Second, 0, access, out pose);
    }

    public CapsuleGeometry ReadStartGeometry(scoped QueryRow row, scoped QueryCursor access, out BodyPose pose)
    {
        ref readonly DynamicEdge edge = ref row.ReadRef<DynamicEdge>();
        return GeometryEndpoints.ReadCapsule(edge.First, edge.Second, 0, access, out pose, GeometryPointTime.Start);
    }

    public void GetStartBodies(scoped QueryRow row, scoped QueryCursor access, Vector3 point, Span<ContactBody> bodies)
    {
        ref readonly DynamicEdge edge = ref row.ReadRef<DynamicEdge>();
        GeometryEndpoints.WriteSegmentBodies(edge.First, edge.Second, access, point, bodies, GeometryPointTime.Start);
    }

    public CollisionBounds ReadMotionBounds(scoped QueryRow row, scoped QueryCursor access, in PhysicsStepContext context, float padding)
    {
        ref readonly DynamicEdge edge = ref row.ReadRef<DynamicEdge>();
        CollisionBounds first = GeometryEndpoints.ReadMotionBounds(edge.First, access, in context, padding);
        CollisionBounds second = GeometryEndpoints.ReadMotionBounds(edge.Second, access, in context, padding);
        return CollisionBounds.Union(in first, in second);
    }

    public int GetBodyCount(scoped QueryRow row, scoped QueryCursor access) => 2;

    public void GetBodies(scoped QueryRow row, scoped QueryCursor access, Vector3 point, Span<ContactBody> bodies)
    {
        ref readonly DynamicEdge edge = ref row.ReadRef<DynamicEdge>();
        GeometryEndpoints.WriteSegmentBodies(edge.First, edge.Second, access, point, bodies);
    }
}
