using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Queries;

namespace SomeEngine.Physics;

/// <summary>A continuous, zero-radius segment between two current body samples.</summary>
public readonly record struct DynamicEdge(GeometryEndpoint First, GeometryEndpoint Second) : IComponent;

public readonly struct DynamicEdgeReader : ICollisionGeometryReader<CapsuleGeometry>
{
    public static bool HasGeometry(scoped QueryRow row) => row.Has<DynamicEdge>();

    public QueryDefinition AddReads(QueryDefinition definition) =>
        definition.WithOptionalRead<DynamicEdge>().WithOptionalRead<BodyPose>();

    public CapsuleGeometry ReadGeometry(scoped QueryRow row, scoped QueryCursor access, out BodyPose pose)
    {
        ref readonly DynamicEdge edge = ref row.ReadRef<DynamicEdge>();
        return GeometryEndpoints.ReadCapsule(edge.First, edge.Second, 0, access, out pose);
    }

    public int GetBodyCount(scoped QueryRow row, scoped QueryCursor access) => 2;

    public void GetBodies(scoped QueryRow row, scoped QueryCursor access, Vector3 point, Span<ContactBody> bodies)
    {
        ref readonly DynamicEdge edge = ref row.ReadRef<DynamicEdge>();
        GeometryEndpoints.WriteSegmentBodies(edge.First, edge.Second, access, point, bodies);
    }
}
