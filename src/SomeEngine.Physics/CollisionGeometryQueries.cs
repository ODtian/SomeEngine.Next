using System.Numerics;
using System.Runtime.CompilerServices;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;

namespace SomeEngine.Physics;

public readonly record struct GeometryVersion(ulong Value) : IComponent;

public static partial class Collision
{
    public static CollisionGeometryQueries<TGeometry> Query<TGeometry>(World world, QueryHandle members,
        CollisionTree tree, CollisionQuerySettings settings)
        where TGeometry : struct, ICollisionGeometry<TGeometry> => new(world, members, tree, settings);

    public static CollisionGeometryQueries<TGeometry, TReader> Query<TGeometry, TReader>(World world,
        QueryHandle members, CollisionTree tree, TReader reader, CollisionQuerySettings settings)
        where TGeometry : struct, ICollisionGeometry<TGeometry>
        where TReader : struct, ICollisionGeometryReader<TGeometry> => new(world, members, tree, reader, settings);
}

public sealed class CollisionGeometryQueries<TGeometry>(World world, QueryHandle members, CollisionTree tree,
    CollisionQuerySettings settings)
    : CollisionGeometryQueries<TGeometry, ComponentGeometry<TGeometry>>(world, members, tree, default, settings)
    where TGeometry : struct, ICollisionGeometry<TGeometry>;

/// <summary>Queries current concrete geometry readers through a maintained collision tree.</summary>
public partial class CollisionGeometryQueries<TGeometry, TReader> : ICollisionQueries, IDisposable
    where TGeometry : struct, ICollisionGeometry<TGeometry>
    where TReader : struct, ICollisionGeometryReader<TGeometry>
{
    private readonly World _world;
    private readonly QueryHandle _query;
    private readonly QueryHandle _members;
    private readonly TReader _reader;
    private readonly CollisionQuerySettings _settings;
    public CollisionTree Tree { get; }

    public CollisionGeometryQueries(World world, QueryHandle members, CollisionTree tree, TReader reader,
        CollisionQuerySettings settings)
    {
        _world = world;
        QueryDefinition membership = world.GetQueryDefinition(members);
        _members = world.Query(membership);
        _query = world.Query(reader.AddReads(QueryDefinition.Empty).WithOptionalRead<GeometryVersion>()
            .WithAccessFrom(membership));
        _reader = reader;
        Tree = tree;
        _settings = settings;
    }

    public bool Raycast(in CollisionRay ray, out CollisionHit hit)
    {
        Span<CollisionRay> rays = stackalloc CollisionRay[1] { ray };
        Span<CollisionHit> hits = stackalloc CollisionHit[1];
        Span<bool> found = stackalloc bool[1];
        Raycast(rays, hits, found);
        hit = hits[0];
        return found[0];
    }

    public void Raycast(ReadOnlySpan<CollisionRay> rays, Span<CollisionHit> hits, Span<bool> found)
    {
        if (rays.IsEmpty) return;
        hits[..rays.Length].Clear();
        found[..rays.Length].Clear();
        var execution = new ReadRays(Tree, rays, hits, found, _settings);
        ExecuteQueries(ref execution);
    }

    public bool SweepSphere(in CollisionSphereSweep sweep, out CollisionHit hit)
    {
        Span<CollisionSphereSweep> sweeps = stackalloc CollisionSphereSweep[1] { sweep };
        Span<CollisionHit> hits = stackalloc CollisionHit[1];
        Span<bool> found = stackalloc bool[1];
        SweepSphere(sweeps, hits, found);
        hit = hits[0];
        return found[0];
    }

    public void SweepSphere(ReadOnlySpan<CollisionSphereSweep> sweeps, Span<CollisionHit> hits, Span<bool> found)
    {
        if (sweeps.IsEmpty) return;
        hits[..sweeps.Length].Clear();
        found[..sweeps.Length].Clear();
        var execution = new ReadSphereSweeps(Tree, sweeps, hits, found, _settings);
        ExecuteQueries(ref execution);
    }

    public bool TryGetTransform(Entity entity, out Matrix4x4 transform, out ulong surfaceVersion)
    {
        var execution = new ReadTransform(entity);
        ExecuteQueries(ref execution);
        transform = execution.Transform;
        surfaceVersion = execution.Version;
        return execution.Found;
    }

    public void Dispose()
    {
        _world.ReleaseQuery(_query);
        _world.ReleaseQuery(_members);
    }

    private void ExecuteQueries<TExecution>(ref TExecution execution)
        where TExecution : IGeometryQueryExecution, allows ref struct
    {
        var read = new ReadAccess<TExecution>(_world, _members, _reader, new(ref execution));
        _world.ExecuteQuery(_query, ref read);
    }

    private interface IGeometryQueryExecution
    {
        void Execute(scoped QueryCursor members, scoped QueryCursor access, TReader reader);
    }

    private readonly ref struct QueryExecutionReference<TExecution> where TExecution : allows ref struct
    {
        // The same managed-address pattern as relation output preserves caller storage through nested borrows.
        private readonly ref byte _value;
        public QueryExecutionReference(ref TExecution execution) => _value = ref Unsafe.As<TExecution, byte>(ref execution);
        public ref TExecution Value => ref Unsafe.As<byte, TExecution>(ref _value);
    }

    private ref struct ReadAccess<TExecution>(World world, QueryHandle members, TReader reader,
        QueryExecutionReference<TExecution> work)
        : IQueryExecution where TExecution : IGeometryQueryExecution, allows ref struct
    {
        private readonly QueryExecutionReference<TExecution> _work = work;

        public void Execute(scoped QueryCursor access)
        {
            var read = new ReadMembers<TExecution>(access, reader, _work);
            world.ExecuteQuery(members, ref read);
        }
    }

    private ref struct ReadMembers<TExecution>(QueryCursor access, TReader reader, QueryExecutionReference<TExecution> work)
        : IQueryExecution where TExecution : IGeometryQueryExecution, allows ref struct
    {
        private readonly QueryCursor _access = access;
        private readonly QueryExecutionReference<TExecution> _work = work;
        public void Execute(scoped QueryCursor members) => _work.Value.Execute(members, _access, reader);
    }

    private static Matrix4x4 Transform(in BodyPose pose)
    {
        Matrix4x4 transform = Matrix4x4.CreateFromQuaternion(pose.Rotation);
        transform.Translation = pose.Position;
        return transform;
    }

    private static ulong Version(scoped QueryRow row) => row.Has<GeometryVersion>() ? row.Read<GeometryVersion>().Value : 0;

    private interface IGeometryQueryWork
    {
        bool Intersects(in CollisionBounds bounds);
        void Visit(scoped QueryRow row, scoped in TGeometry geometry, scoped in BodyPose pose);
    }

    private ref struct ReadGeometry<TWork>(CollisionTree tree, TWork work) : IGeometryQueryExecution
        where TWork : IGeometryQueryWork, allows ref struct
    {
        public TWork Work = work;

        public void Execute(scoped QueryCursor members, scoped QueryCursor access, TReader reader)
        {
            if (tree.Root < 0) return;
            ReadOnlySpan<CollisionNode> nodes = tree.Nodes;
            Span<int> remaining = stackalloc int[tree.Height + 1];
            int count = 0;
            remaining[count++] = tree.Root;
            while (count > 0)
            {
                ref readonly CollisionNode node = ref nodes[remaining[--count]];
                if (!Work.Intersects(in node.Bounds)) continue;
                if (!node.IsLeaf)
                {
                    remaining[count++] = node.FirstChild;
                    remaining[count++] = node.SecondChild;
                    continue;
                }
                if (!members.Contains(node.Entity) || !access.TryGetRow(node.Entity, out QueryRow row)
                    || !TReader.HasGeometry(row)) continue;
                TGeometry geometry = reader.ReadGeometry(row, access, out BodyPose pose);
                Work.Visit(row, in geometry, in pose);
            }
        }
    }

    private readonly ref struct RayBatch(ReadOnlySpan<CollisionRay> rays, Span<CollisionHit> hits,
        Span<bool> found, CollisionQuerySettings settings) : IGeometryQueryWork
    {
        private readonly ReadOnlySpan<CollisionRay> _rays = rays;
        private readonly Span<CollisionHit> _hits = hits;
        private readonly Span<bool> _found = found;
        private readonly CollisionBounds _bounds = RayBounds(rays);

        public bool Intersects(in CollisionBounds bounds) => _bounds.Overlaps(in bounds);

        public void Visit(scoped QueryRow row, scoped in TGeometry geometry, scoped in BodyPose pose)
        {
            for (int index = 0; index < _rays.Length; index++)
            {
                CollisionRay ray = _rays[index];
                var sweep = new CollisionSphereSweep(ray.Origin, 0, ray.Direction,
                    _found[index] ? _hits[index].Distance : ray.Length);
                if (!TGeometry.CastSphere(in geometry, in pose, in sweep, in settings, out GeometryCastHit hit)) continue;
                _hits[index] = new(row.Entity, hit.Point, hit.Normal, hit.Distance, Transform(in pose), Version(row))
                {
                    Feature = hit.Feature,
                };
                _found[index] = true;
            }
        }
    }

    private readonly ref struct ReadRays(CollisionTree tree, ReadOnlySpan<CollisionRay> rays, Span<CollisionHit> hits,
        Span<bool> found, CollisionQuerySettings settings) : IGeometryQueryExecution
    {
        private readonly ReadOnlySpan<CollisionRay> _rays = rays;
        private readonly Span<CollisionHit> _hits = hits;
        private readonly Span<bool> _found = found;

        public void Execute(scoped QueryCursor members, scoped QueryCursor access, TReader reader)
        {
            for (int index = 0; index < _rays.Length; index++)
            {
                var work = new RayBatch(_rays.Slice(index, 1), _hits.Slice(index, 1), _found.Slice(index, 1), settings);
                var execution = new ReadGeometry<RayBatch>(tree, work);
                execution.Execute(members, access, reader);
            }
        }
    }

    private readonly ref struct SphereBatch(ReadOnlySpan<CollisionSphereSweep> sweeps, Span<CollisionHit> hits,
        Span<bool> found, CollisionQuerySettings settings) : IGeometryQueryWork
    {
        private readonly ReadOnlySpan<CollisionSphereSweep> _sweeps = sweeps;
        private readonly Span<CollisionHit> _hits = hits;
        private readonly Span<bool> _found = found;
        private readonly CollisionBounds _bounds = SweepBounds(sweeps);

        public bool Intersects(in CollisionBounds bounds) => _bounds.Overlaps(in bounds);

        public void Visit(scoped QueryRow row, scoped in TGeometry geometry, scoped in BodyPose pose)
        {
            for (int index = 0; index < _sweeps.Length; index++)
            {
                CollisionSphereSweep sweep = _sweeps[index];
                if (_found[index]) sweep = sweep with { Length = _hits[index].Distance };
                if (!TGeometry.CastSphere(in geometry, in pose, in sweep, in settings, out GeometryCastHit hit)) continue;
                _hits[index] = new(row.Entity, hit.Point, hit.Normal, hit.Distance, Transform(in pose), Version(row))
                {
                    Feature = hit.Feature,
                };
                _found[index] = true;
            }
        }
    }

    private readonly ref struct ReadSphereSweeps(CollisionTree tree, ReadOnlySpan<CollisionSphereSweep> sweeps,
        Span<CollisionHit> hits, Span<bool> found, CollisionQuerySettings settings) : IGeometryQueryExecution
    {
        private readonly ReadOnlySpan<CollisionSphereSweep> _sweeps = sweeps;
        private readonly Span<CollisionHit> _hits = hits;
        private readonly Span<bool> _found = found;

        public void Execute(scoped QueryCursor members, scoped QueryCursor access, TReader reader)
        {
            for (int index = 0; index < _sweeps.Length; index++)
            {
                var work = new SphereBatch(_sweeps.Slice(index, 1), _hits.Slice(index, 1), _found.Slice(index, 1), settings);
                var execution = new ReadGeometry<SphereBatch>(tree, work);
                execution.Execute(members, access, reader);
            }
        }
    }

    private struct ReadTransform(Entity entity) : IGeometryQueryExecution
    {
        internal Matrix4x4 Transform;
        internal ulong Version;
        internal bool Found;

        public void Execute(scoped QueryCursor members, scoped QueryCursor access, TReader reader)
        {
            if (!members.Contains(entity) || !access.TryGetRow(entity, out QueryRow row) || !TReader.HasGeometry(row)) return;
            reader.ReadGeometry(row, access, out BodyPose pose);
            Transform = CollisionGeometryQueries<TGeometry, TReader>.Transform(in pose);
            Version = CollisionGeometryQueries<TGeometry, TReader>.Version(row);
            Found = true;
        }
    }

    private static CollisionBounds RayBounds(ReadOnlySpan<CollisionRay> rays)
    {
        Vector3 minimum = rays[0].Origin, maximum = minimum;
        foreach (CollisionRay ray in rays)
        {
            Vector3 end = ray.Origin + ray.Direction * ray.Length;
            minimum = Vector3.Min(minimum, Vector3.Min(ray.Origin, end));
            maximum = Vector3.Max(maximum, Vector3.Max(ray.Origin, end));
        }
        return new(minimum, maximum);
    }

    private static CollisionBounds SweepBounds(ReadOnlySpan<CollisionSphereSweep> sweeps)
    {
        Vector3 minimum = sweeps[0].Center, maximum = minimum;
        foreach (CollisionSphereSweep sweep in sweeps)
        {
            Vector3 end = sweep.Center + sweep.Direction * sweep.Length;
            minimum = Vector3.Min(minimum, Vector3.Min(sweep.Center, end) - new Vector3(sweep.Radius));
            maximum = Vector3.Max(maximum, Vector3.Max(sweep.Center, end) + new Vector3(sweep.Radius));
        }
        return new(minimum, maximum);
    }
}
