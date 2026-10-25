using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;

namespace SomeEngine.Physics;

/// <summary>Shared local-space triangles whose arrays remain read-only during use.</summary>
public sealed class CollisionMesh
{
    public ReadOnlyMemory<Vector3> Positions { get; }
    public ReadOnlyMemory<uint> Indices { get; }
    public CollisionBounds Bounds { get; }

    public CollisionMesh(ReadOnlyMemory<Vector3> positions, ReadOnlyMemory<uint> indices)
    {
        Positions = positions;
        Indices = indices;
        Vector3 min = positions.Span[0], max = min;
        foreach (Vector3 point in positions.Span)
        {
            min = Vector3.Min(min, point);
            max = Vector3.Max(max, point);
        }
        Bounds = new(min, max);
    }
}

/// <summary>A scene mesh, its current local-to-world transform, and caller-provided surface revision.</summary>
public readonly record struct CollisionSurface(CollisionMesh Mesh, Matrix4x4 Transform, ulong Version) : IComponent
{
    public CollisionBounds Bounds
    {
        get
        {
            CollisionBounds local = Mesh.Bounds;
            Vector3 min = Vector3.Transform(local.Min, Transform), max = min;
            for (int corner = 1; corner < 8; corner++)
            {
                Vector3 point = Vector3.Transform(new(
                    (corner & 1) == 0 ? local.Min.X : local.Max.X,
                    (corner & 2) == 0 ? local.Min.Y : local.Max.Y,
                    (corner & 4) == 0 ? local.Min.Z : local.Max.Z), Transform);
                min = Vector3.Min(min, point);
                max = Vector3.Max(max, point);
            }
            return new(min, max);
        }
    }
}

/// <summary>Reads a caller's concrete scene component through the same collision-tree input.</summary>
public interface ICollisionSurfaceReader<TComponent> : ICollisionBoundsReader<TComponent>
{
    CollisionSurface ReadSurface(in TComponent component);
}

public readonly struct CollisionSurfaceReader : ICollisionSurfaceReader<CollisionSurface>
{
    public CollisionBounds Read(in CollisionSurface component) => component.Bounds;
    public CollisionSurface ReadSurface(in CollisionSurface component) => component;
}

/// <summary>A world-space segment with a unit direction and a distance in world units.</summary>
public readonly record struct CollisionRay(Vector3 Origin, Vector3 Direction, float Length);

/// <summary>A world-space sphere swept along a unit direction by a distance in world units.</summary>
public readonly record struct CollisionSphereSweep(Vector3 Center, float Radius, Vector3 Direction, float Length);

/// <summary>A scene hit in world coordinates, retaining the support's current transform and revision.</summary>
public readonly record struct CollisionHit(Entity Entity, Vector3 Point, Vector3 Normal, float Distance,
    Matrix4x4 Transform, ulong SurfaceVersion)
{
    public int Feature { get; init; }
}
