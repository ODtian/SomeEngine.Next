using System.Numerics;
using SomeEngine.ECS.Queries;

namespace SomeEngine.Physics;

/// <summary>Reads current geometry and the participating simulation endpoints from an active query.</summary>
public interface ICollisionGeometryReader<TGeometry> where TGeometry : struct
{
    static virtual bool HasGeometry(scoped QueryRow row) => true;
    QueryDefinition AddReads(QueryDefinition definition);
    TGeometry ReadGeometry(scoped QueryRow row, scoped QueryCursor access, out BodyPose pose);
    int GetBodyCount(scoped QueryRow row, scoped QueryCursor access);
    void GetBodies(scoped QueryRow row, scoped QueryCursor access, Vector3 point, Span<ContactBody> bodies);
}

public readonly struct ComponentGeometry<TGeometry>(ContactNormalReference normalReference = ContactNormalReference.MaterialPoint)
    : ICollisionGeometryReader<TGeometry> where TGeometry : struct
{
    public static bool HasGeometry(scoped QueryRow row) => row.Has<TGeometry>();

    public QueryDefinition AddReads(QueryDefinition definition) =>
        definition.WithOptionalRead<TGeometry>().WithOptionalRead<BodyPose>();

    public TGeometry ReadGeometry(scoped QueryRow row, scoped QueryCursor access, out BodyPose pose)
    {
        pose = row.Read<BodyPose>();
        return row.Read<TGeometry>();
    }

    public int GetBodyCount(scoped QueryRow row, scoped QueryCursor access) => 1;

    public void GetBodies(scoped QueryRow row, scoped QueryCursor access, Vector3 point, Span<ContactBody> bodies) =>
        bodies[0] = new(row.Entity, GeometrySpace.ToLocal(in row.ReadRef<BodyPose>(), point), 1, normalReference);
}
