using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;

namespace SomeEngine.Physics;

public static partial class Collision
{
    public static CollisionQueries<CollisionSurface, CollisionSurfaceReader> Query(
        World world, QueryHandle members, CollisionTree tree) => new(world, members, tree, default);

    public static CollisionQueries<TComponent, TReader> Query<TComponent, TReader>(
        World world, QueryHandle members, CollisionTree tree, TReader reader)
        where TComponent : struct
        where TReader : struct, ICollisionSurfaceReader<TComponent> => new(world, members, tree, reader);
}

/// <summary>Queries current World surfaces through a caller-held collision tree.</summary>
public sealed partial class CollisionQueries<TComponent, TReader> : ICollisionQueries, IDisposable
    where TComponent : struct
    where TReader : struct, ICollisionSurfaceReader<TComponent>
{
    private readonly World _world;
    private readonly QueryHandle _query;
    private readonly TReader _reader;
    public CollisionTree Tree { get; }

    public CollisionQueries(World world, QueryHandle members, CollisionTree tree, TReader reader)
    {
        _world = world;
        _query = world.Query(world.GetQueryDefinition(members).WithOptionalRead<TComponent>());
        Tree = tree;
        _reader = reader;
    }

    /// <summary>Returns the nearest intersected triangle along the ray.</summary>
    public bool Raycast(in CollisionRay ray, out CollisionHit hit)
    {
        var execution = new CastExecution<RayCast>(Tree, _reader, new(ray));
        _world.ExecuteQuery(_query, ref execution);
        hit = execution.Hit;
        return execution.Found;
    }

    /// <summary>Writes the nearest hit and hit status for every input ray in one World read scope.</summary>
    public void Raycast(ReadOnlySpan<CollisionRay> rays, Span<CollisionHit> hits, Span<bool> found)
    {
        var execution = new RayBatchExecution(Tree, _reader, rays, hits, found);
        _world.ExecuteQuery(_query, ref execution);
    }

    /// <summary>Returns the first contact of the swept sphere with the current scene triangles.</summary>
    public bool SweepSphere(in CollisionSphereSweep sweep, out CollisionHit hit)
    {
        var execution = new CastExecution<SphereCast>(Tree, _reader, new(sweep));
        _world.ExecuteQuery(_query, ref execution);
        hit = execution.Hit;
        return execution.Found;
    }

    /// <summary>Reads the current transform and geometry of a support entity in this query.</summary>
    public bool TryGetSurface(Entity entity, out CollisionSurface surface)
    {
        var execution = new SurfaceExecution(entity, _reader);
        _world.ExecuteQuery(_query, ref execution);
        surface = execution.Surface;
        return execution.Found;
    }

    public void Dispose() => _world.ReleaseQuery(_query);

    private readonly ref struct RayBatchExecution(CollisionTree tree, TReader reader,
        ReadOnlySpan<CollisionRay> rays, Span<CollisionHit> hits, Span<bool> found) : IQueryExecution
    {
        private readonly ReadOnlySpan<CollisionRay> _rays = rays;
        private readonly Span<CollisionHit> _hits = hits;
        private readonly Span<bool> _found = found;

        public void Execute(QueryCursor query)
        {
            for (int index = 0; index < _rays.Length; index++)
            {
                var cast = new CastExecution<RayCast>(tree, reader, new(_rays[index]));
                cast.Execute(query);
                _hits[index] = cast.Hit;
                _found[index] = cast.Found;
            }
        }
    }

    private struct SurfaceExecution(Entity entity, TReader reader) : IQueryExecution
    {
        internal CollisionSurface Surface;
        internal bool Found;

        public void Execute(QueryCursor query)
        {
            if (!query.TryGetRow(entity, out var row)) return;
            Surface = reader.ReadSurface(in row.ReadRef<TComponent>());
            Found = true;
        }
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

    private struct CastExecution<TCast>(CollisionTree tree, TReader reader, TCast cast) : IQueryExecution
        where TCast : struct, IShapeCast
    {
        internal CollisionHit Hit;
        internal bool Found;

        public void Execute(QueryCursor query)
        {
            if (tree.Root < 0) return;
            CollisionRay ray = cast.Ray;
            Vector3 radius = new(cast.Radius);
            Vector3 end = ray.Origin + ray.Direction * ray.Length;
            var range = new CollisionBounds(Vector3.Min(ray.Origin, end) - radius, Vector3.Max(ray.Origin, end) + radius);
            float nearest = ray.Length;
            ReadOnlySpan<CollisionNode> nodes = tree.Nodes;
            Span<int> work = stackalloc int[tree.Height + 1];
            int count = 0;
            work[count++] = tree.Root;
            while (count > 0)
            {
                ref readonly CollisionNode node = ref nodes[work[--count]];
                if (!node.Bounds.Overlaps(in range)) continue;
                if (!node.IsLeaf)
                {
                    work[count++] = node.SecondChild;
                    work[count++] = node.FirstChild;
                    continue;
                }
                if (!query.TryGetRow(node.Entity, out var row)) continue;
                CollisionSurface surface = reader.ReadSurface(in row.ReadRef<TComponent>());
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
