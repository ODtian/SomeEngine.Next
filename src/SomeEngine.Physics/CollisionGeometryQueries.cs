using System.Numerics;
using System.Runtime.CompilerServices;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Physics;

public readonly record struct GeometryVersion(ulong Value) : IComponent;

public static partial class Collision
{
    public static CollisionGeometryQueries<TGeometry> Query<TGeometry>(World world, Query members,
        BoundsTree tree, CollisionQuerySettings settings) where TGeometry : struct, ICollisionGeometry<TGeometry> =>
        new(world, members, tree, settings);
    public static CollisionGeometryQueries<TGeometry, ResourceGeometryReader<TGeometry, TResources>> QueryWithResources<TGeometry, TResources>(
        World world, Query members, BoundsTree tree, CollisionQuerySettings settings)
        where TGeometry : struct, ICollisionGeometry<TGeometry> where TResources : struct, IJobComponentResources<TGeometry> =>
        new(world, members, tree, default, settings);
    public static CollisionGeometryQueries<TGeometry, TReader> Query<TGeometry, TReader>(World world, Query members,
        BoundsTree tree, TReader reader, CollisionQuerySettings settings)
        where TGeometry : struct, ICollisionGeometry<TGeometry> where TReader : struct, ICollisionGeometryReader<TGeometry> =>
        new(world, members, tree, reader, settings);
}

public sealed class CollisionGeometryQueries<TGeometry>(World world, Query members, BoundsTree tree, CollisionQuerySettings settings)
    : CollisionGeometryQueries<TGeometry, ComponentGeometry<TGeometry>>(world, members, tree, default, settings)
    where TGeometry : struct, ICollisionGeometry<TGeometry>;

/// <summary>Queries current geometry through a caller-held tree and readonly source permissions.</summary>
public partial class CollisionGeometryQueries<TGeometry, TReader> : ICollisionQueries
    where TGeometry : struct, ICollisionGeometry<TGeometry> where TReader : struct, ICollisionGeometryReader<TGeometry>
{
    private readonly World _world;
    private readonly Query _query, _members;
    private readonly TReader _reader;
    private readonly CollisionQuerySettings _settings;
    public BoundsTree Tree { get; }
    public CollisionGeometryQueries(World world, Query members, BoundsTree tree, TReader reader, CollisionQuerySettings settings)
    {
        _world = world; _members = members;
        _query = world.Query(reader.AddReads(new QueryDescription()).Optional<GeometryVersion>());
        _reader = reader; Tree = tree; _settings = settings;
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

    private void ExecuteQueries<TExecution>(ref TExecution execution)
        where TExecution : IGeometryQueryExecution, allows ref struct
    {
        using QueryScope access = _query.OpenRead();
        using QueryScope members = _members.OpenRead();
        if (_world.Execution is WorldJobs jobs)
        {
            var reads = new List<SomeEngine.Job.JobResourceAccess>();
            _reader.AddAccesses(_world, reads); reads.Add(jobs.Read<GeometryVersion>());
            var work = new QueryWork<TExecution>(_world, members, _reader, execution);
            jobs.Scheduler.Execute(ref work, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(reads));
            execution = work.Work;
        }
        else execution.Execute(_world, members, _reader);
    }
    private interface IGeometryQueryExecution
    {
        void Execute(World world, scoped QueryScope members, TReader reader);
    }
    private ref struct QueryWork<TExecution>(World world, QueryScope members, TReader reader, TExecution work) : SomeEngine.Job.IJob
        where TExecution : IGeometryQueryExecution, allows ref struct
    {
        private readonly QueryScope _members = members;
        internal TExecution Work = work;
        public void Execute() => Work.Execute(world, _members, reader);
    }

    private static Matrix4x4 Transform(in BodyPose pose)
    {
        Matrix4x4 transform = Matrix4x4.CreateFromQuaternion(pose.Rotation);
        transform.Translation = pose.Position;
        return transform;
    }

    private static ulong Version(World world, Entity entity) => world.Has<GeometryVersion>(entity) ? world.Read<GeometryVersion>(entity).Value : 0;

    private interface IGeometryQueryWork
    {
        bool Intersects(in CollisionBounds bounds);
        void Visit(World world, Entity entity, scoped in TGeometry geometry, scoped in BodyPose pose);
    }

    private ref struct ReadGeometry<TWork>(BoundsTree tree, TWork work) : IGeometryQueryExecution
        where TWork : IGeometryQueryWork, allows ref struct
    {
        public TWork Work = work;

        public void Execute(World world, scoped QueryScope members, TReader reader)
        {
            if (tree.Root < 0) return;
            ReadOnlySpan<BoundsNode> nodes = tree.Nodes;
            Span<int> remaining = stackalloc int[tree.Height + 1];
            int count = 0;
            remaining[count++] = tree.Root;
            while (count > 0)
            {
                ref readonly BoundsNode node = ref nodes[remaining[--count]];
                var bounds = new CollisionBounds(node.Bounds.Min, node.Bounds.Max);
                if (!Work.Intersects(in bounds)) continue;
                if (!node.IsLeaf)
                {
                    remaining[count++] = node.FirstChild;
                    remaining[count++] = node.SecondChild;
                    continue;
                }
                if (!members.Contains(node.Entity) || !TReader.HasGeometry(world, node.Entity)) continue;
                TGeometry geometry = reader.ReadGeometry(world, node.Entity, out BodyPose pose);
                Work.Visit(world, node.Entity, in geometry, in pose);
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

        public void Visit(World world, Entity entity, scoped in TGeometry geometry, scoped in BodyPose pose)
        {
            for (int index = 0; index < _rays.Length; index++)
            {
                CollisionRay ray = _rays[index];
                var sweep = new CollisionSphereSweep(ray.Origin, 0, ray.Direction,
                    _found[index] ? _hits[index].Distance : ray.Length);
                if (!TGeometry.CastSphere(in geometry, in pose, in sweep, in settings, out GeometryCastHit hit)) continue;
                _hits[index] = new(entity, hit.Point, hit.Normal, hit.Distance, Transform(in pose), Version(world, entity))
                {
                    Feature = hit.Feature,
                };
                _found[index] = true;
            }
        }
    }

    private readonly ref struct ReadRays(BoundsTree tree, ReadOnlySpan<CollisionRay> rays, Span<CollisionHit> hits,
        Span<bool> found, CollisionQuerySettings settings) : IGeometryQueryExecution
    {
        private readonly ReadOnlySpan<CollisionRay> _rays = rays;
        private readonly Span<CollisionHit> _hits = hits;
        private readonly Span<bool> _found = found;

        public void Execute(World world, scoped QueryScope members, TReader reader)
        {
            for (int index = 0; index < _rays.Length; index++)
            {
                var work = new RayBatch(_rays.Slice(index, 1), _hits.Slice(index, 1), _found.Slice(index, 1), settings);
                var execution = new ReadGeometry<RayBatch>(tree, work);
                execution.Execute(world, members, reader);
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

        public void Visit(World world, Entity entity, scoped in TGeometry geometry, scoped in BodyPose pose)
        {
            for (int index = 0; index < _sweeps.Length; index++)
            {
                CollisionSphereSweep sweep = _sweeps[index];
                if (_found[index]) sweep = sweep with { Length = _hits[index].Distance };
                if (!TGeometry.CastSphere(in geometry, in pose, in sweep, in settings, out GeometryCastHit hit)) continue;
                _hits[index] = new(entity, hit.Point, hit.Normal, hit.Distance, Transform(in pose), Version(world, entity))
                {
                    Feature = hit.Feature,
                };
                _found[index] = true;
            }
        }
    }

    private readonly ref struct ReadSphereSweeps(BoundsTree tree, ReadOnlySpan<CollisionSphereSweep> sweeps,
        Span<CollisionHit> hits, Span<bool> found, CollisionQuerySettings settings) : IGeometryQueryExecution
    {
        private readonly ReadOnlySpan<CollisionSphereSweep> _sweeps = sweeps;
        private readonly Span<CollisionHit> _hits = hits;
        private readonly Span<bool> _found = found;

        public void Execute(World world, scoped QueryScope members, TReader reader)
        {
            for (int index = 0; index < _sweeps.Length; index++)
            {
                var work = new SphereBatch(_sweeps.Slice(index, 1), _hits.Slice(index, 1), _found.Slice(index, 1), settings);
                var execution = new ReadGeometry<SphereBatch>(tree, work);
                execution.Execute(world, members, reader);
            }
        }
    }

    private struct ReadTransform(Entity entity) : IGeometryQueryExecution
    {
        internal Matrix4x4 Transform;
        internal ulong Version;
        internal bool Found;

        public void Execute(World world, scoped QueryScope members, TReader reader)
        {
            if (!members.Contains(entity) || !TReader.HasGeometry(world, entity)) return;
            reader.ReadGeometry(world, entity, out BodyPose pose);
            Transform = CollisionGeometryQueries<TGeometry, TReader>.Transform(in pose);
            Version = CollisionGeometryQueries<TGeometry, TReader>.Version(world, entity);
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
