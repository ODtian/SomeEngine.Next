using SomeEngine.ECS.Spatial;
using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;

namespace SomeEngine.Physics;

public static partial class Collision
{
    public static CollisionQueries<CollisionSurface, CollisionSurfaceReader> Query(
        World world, Query members, BoundsTree tree) => new(world, members, tree, default);

    public static CollisionQueries<TComponent, TReader> Query<TComponent, TReader>(
        World world, Query members, BoundsTree tree, TReader reader)
        where TComponent : struct
        where TReader : struct, ICollisionSurfaceReader<TComponent> => new(world, members, tree, reader);
}

/// <summary>Queries current World surfaces through a caller-held collision tree.</summary>
public sealed partial class CollisionQueries<TComponent, TReader> : ICollisionQueries
    where TComponent : struct
    where TReader : struct, ICollisionSurfaceReader<TComponent>
{
    private readonly World _world;
    private readonly Query _query;
    private readonly TReader _reader;
    public BoundsTree Tree { get; }

    public CollisionQueries(World world, Query members, BoundsTree tree, TReader reader)
    {
        _world = world;
        _query = members.WithOptionalRead<TComponent>();
        Tree = tree;
        _reader = reader;
    }

    /// <summary>Returns the nearest intersected triangle along the ray.</summary>
    public bool Raycast(in CollisionRay ray, out CollisionHit hit)
    {
        using QueryScope scope = _query.OpenRead();
        using ReadLookup<TComponent> values = _world.OpenRead<TComponent>();
        var cast = new CastExecution<RayCast>(Tree, _reader, new(ray));
        cast.Execute(scope, values);
        hit = cast.Hit;
        return cast.Found;
    }

    public void Raycast(ReadOnlySpan<CollisionRay> rays, Span<CollisionHit> hits, Span<bool> found)
    {
        using QueryScope scope = _query.OpenRead();
        using ReadLookup<TComponent> values = _world.OpenRead<TComponent>();
        for (int index = 0; index < rays.Length; index++)
        {
            var cast = new CastExecution<RayCast>(Tree, _reader, new(rays[index]));
            cast.Execute(scope, values);
            hits[index] = cast.Hit;
            found[index] = cast.Found;
        }
    }

    /// <summary>Returns the first contact of the swept sphere with the current scene triangles.</summary>
    public bool SweepSphere(in CollisionSphereSweep sweep, out CollisionHit hit)
    {
        using QueryScope scope = _query.OpenRead();
        using ReadLookup<TComponent> values = _world.OpenRead<TComponent>();
        var cast = new CastExecution<SphereCast>(Tree, _reader, new(sweep));
        cast.Execute(scope, values);
        hit = cast.Hit;
        return cast.Found;
    }

    public void SweepSphere(ReadOnlySpan<CollisionSphereSweep> sweeps, Span<CollisionHit> hits, Span<bool> found)
    {
        using QueryScope scope = _query.OpenRead();
        using ReadLookup<TComponent> values = _world.OpenRead<TComponent>();
        for (int index = 0; index < sweeps.Length; index++)
        {
            var cast = new CastExecution<SphereCast>(Tree, _reader, new(sweeps[index]));
            cast.Execute(scope, values);
            hits[index] = cast.Hit;
            found[index] = cast.Found;
        }
    }

    public bool TryGetSurface(Entity entity, out CollisionSurface surface)
    {
        using QueryScope scope = _query.OpenRead();
        using ReadLookup<TComponent> values = _world.OpenRead<TComponent>();
        if (scope.Contains(entity) && values.Has(entity))
        {
            surface = _reader.ReadSurface(in values.ReadKnown(entity));
            return true;
        }
        surface = default;
        return false;
    }

    public bool TryGetTransform(Entity entity, out Matrix4x4 transform, out ulong surfaceVersion)
    {
        bool found = TryGetSurface(entity, out CollisionSurface surface);
        transform = surface.Transform;
        surfaceVersion = surface.Version;
        return found;
    }

    private interface IShapeCast
    {
        CollisionRay Ray { get; }
        float Radius { get; }
        bool Intersect(Vector3 a, Vector3 b, Vector3 c, float maximum,
            out float distance, out Vector3 point, out Vector3 normal);
    }

    private readonly struct RayCast(CollisionRay ray) : IShapeCast
    {
        public CollisionRay Ray => ray;
        public float Radius => 0;
        public bool Intersect(Vector3 a, Vector3 b, Vector3 c, float maximum,
            out float distance, out Vector3 point, out Vector3 normal)
        {
            bool found = CollisionTriangles.Raycast(in ray, a, b, c, maximum, out distance, out normal);
            point = ray.Origin + ray.Direction * distance;
            return found;
        }
    }

    private readonly struct SphereCast(CollisionSphereSweep sweep) : IShapeCast
    {
        public CollisionRay Ray => new(sweep.Center, sweep.Direction, sweep.Length);
        public float Radius => sweep.Radius;
        public bool Intersect(Vector3 a, Vector3 b, Vector3 c, float maximum,
            out float distance, out Vector3 point, out Vector3 normal)
            => CollisionTriangles.SweepSphere(in sweep, a, b, c, maximum, out distance, out point, out normal);
    }

    private struct CastExecution<TCast>(BoundsTree tree, TReader reader, TCast cast)
        where TCast : struct, IShapeCast
    {
        internal CollisionHit Hit;
        internal bool Found;

        public void Execute(scoped QueryScope query, scoped ReadLookup<TComponent> values)
        {
            if (tree.Root < 0) return;
            CollisionRay ray = cast.Ray;
            Vector3 radius = new(cast.Radius);
            Vector3 end = ray.Origin + ray.Direction * ray.Length;
            var range = new SpatialBounds(Vector3.Min(ray.Origin, end) - radius, Vector3.Max(ray.Origin, end) + radius);
            float nearest = ray.Length;
            ReadOnlySpan<BoundsNode> nodes = tree.Nodes;
            Span<int> work = stackalloc int[tree.Height + 1];
            int count = 0;
            work[count++] = tree.Root;
            while (count > 0)
            {
                ref readonly BoundsNode node = ref nodes[work[--count]];
                if (!node.Bounds.Overlaps(in range)) continue;
                if (!node.IsLeaf)
                {
                    work[count++] = node.SecondChild;
                    work[count++] = node.FirstChild;
                    continue;
                }
                if (!query.Contains(node.Entity) || !values.Has(node.Entity)) continue;
                CollisionSurface surface = reader.ReadSurface(in values.ReadKnown(node.Entity));
                ReadOnlySpan<Vector3> points = surface.Mesh.Positions.Span;
                ReadOnlySpan<uint> indices = surface.Mesh.Indices.Span;
                for (int index = 0; index < indices.Length; index += 3)
                {
                    Vector3 a = Vector3.Transform(points[(int)indices[index]], surface.Transform);
                    Vector3 b = Vector3.Transform(points[(int)indices[index + 1]], surface.Transform);
                    Vector3 c = Vector3.Transform(points[(int)indices[index + 2]], surface.Transform);
                    if (!cast.Intersect(a, b, c, nearest, out float distance, out Vector3 point, out Vector3 normal)) continue;
                    nearest = distance;
                    Hit = new(node.Entity, point, normal, distance,
                        surface.Transform, surface.Version);
                    Found = true;
                }
            }
        }
    }
}

internal static partial class CollisionTriangles
{
    internal static bool Raycast(in CollisionRay ray, Vector3 a, Vector3 b, Vector3 c, float maximum,
        out float distance, out Vector3 normal)
    {
        Vector3 first = b - a, second = c - a;
        Vector3 p = Vector3.Cross(ray.Direction, second);
        float determinant = Vector3.Dot(first, p);
        distance = 0;
        normal = default;
        if (MathF.Abs(determinant) < 1e-7f) return false;
        float inverse = 1 / determinant;
        Vector3 offset = ray.Origin - a;
        float u = Vector3.Dot(offset, p) * inverse;
        if (u < 0 || u > 1) return false;
        Vector3 q = Vector3.Cross(offset, first);
        float v = Vector3.Dot(ray.Direction, q) * inverse;
        if (v < 0 || u + v > 1) return false;
        distance = Vector3.Dot(second, q) * inverse;
        if (distance < 0 || distance > maximum) return false;
        normal = Vector3.Normalize(Vector3.Cross(first, second));
        if (Vector3.Dot(normal, ray.Direction) > 0) normal = -normal;
        return true;
    }
}
