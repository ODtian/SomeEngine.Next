using System.Numerics;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;

namespace SomeEngine.Physics;

public readonly record struct CollisionDistanceHit(Entity Entity, CollisionDistance Distance,
    Matrix4x4 Transform, ulong SurfaceVersion);

public sealed partial class CollisionGeometryQueries<TGeometry> where TGeometry : struct, ICollisionGeometry<TGeometry>
{
    public int Overlap<TQuery, TKernel>(in TQuery geometry, in BodyPose pose, TKernel kernel, Span<Entity> matches)
        where TQuery : struct, ICollisionGeometry<TQuery>
        where TKernel : struct, ICollisionNarrowPhase<TQuery, TGeometry>
    {
        var work = new FindOverlaps<TQuery, TKernel>(geometry, pose, kernel, matches);
        var execution = new ReadGeometry<FindOverlaps<TQuery, TKernel>>(Tree, work);
        _world.ExecuteQuery(_query, ref execution);
        return execution.Work.Count;
    }

    /// <summary>Finds the minimum signed gap from a query geometry to the selected scene geometries.</summary>
    public bool Distance<TQuery, TKernel>(in TQuery geometry, in BodyPose pose, TKernel kernel, out CollisionDistanceHit hit)
        where TQuery : struct
        where TKernel : struct, ICollisionNarrowPhase<TQuery, TGeometry>
    {
        var work = new MeasureDistance<TQuery, TKernel>(geometry, pose, kernel);
        var execution = new ReadGeometry<MeasureDistance<TQuery, TKernel>>(Tree, work);
        _world.ExecuteQuery(_query, ref execution);
        hit = execution.Work.Hit;
        return execution.Work.Found;
    }

    private struct MeasureDistance<TQuery, TKernel>(TQuery geometry, BodyPose pose, TKernel kernel) : IGeometryQueryWork
        where TQuery : struct
        where TKernel : struct, ICollisionNarrowPhase<TQuery, TGeometry>
    {
        public CollisionDistanceHit Hit;
        public bool Found;
        public bool Intersects(in CollisionBounds bounds) => true;

        public void Visit(scoped QueryRow row, scoped in TGeometry target, scoped in BodyPose targetPose)
        {
            CollisionDistance distance = kernel.Distance(in geometry, in pose, in target, in targetPose);
            if (Found && distance.Separation >= Hit.Distance.Separation) return;
            Hit = new(row.Entity, distance, Transform(in targetPose), Version(row));
            Found = true;
        }
    }

    private ref struct FindOverlaps<TQuery, TKernel>(TQuery geometry, BodyPose pose, TKernel kernel,
        Span<Entity> matches) : IGeometryQueryWork
        where TQuery : struct, ICollisionGeometry<TQuery>
        where TKernel : struct, ICollisionNarrowPhase<TQuery, TGeometry>
    {
        private readonly Span<Entity> _matches = matches;
        private readonly CollisionBounds _bounds = TQuery.Bounds(in geometry, in pose);
        public int Count;
        public bool Intersects(in CollisionBounds bounds) => _bounds.Overlaps(in bounds);

        public void Visit(scoped QueryRow row, scoped in TGeometry target, scoped in BodyPose targetPose)
        {
            if (kernel.Distance(in geometry, in pose, in target, in targetPose).Separation <= 0)
                _matches[Count++] = row.Entity;
        }
    }
}
