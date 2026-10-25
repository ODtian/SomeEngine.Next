using System.Numerics;
using SomeEngine.ECS.Queries;

namespace SomeEngine.Physics;

public sealed partial class CollisionGeometryQueries<TGeometry> where TGeometry : struct, ICollisionGeometry<TGeometry>
{
    public bool Sweep<TQuery, TSweep>(in TQuery geometry, in BodyPose pose, Vector3 direction, float length,
        TSweep sweep, out CollisionHit hit)
        where TQuery : struct, ICollisionGeometry<TQuery>
        where TSweep : struct, ICollisionSweep<TQuery, TGeometry>
    {
        var work = new SweepGeometry<TQuery, TSweep>(geometry, pose, direction, length, sweep);
        var execution = new ReadGeometry<SweepGeometry<TQuery, TSweep>>(Tree, work);
        _world.ExecuteQuery(_query, ref execution);
        hit = execution.Work.Hit;
        return execution.Work.Found;
    }

    private struct SweepGeometry<TQuery, TSweep>(TQuery geometry, BodyPose pose, Vector3 direction, float length,
        TSweep sweep) : IGeometryQueryWork
        where TQuery : struct, ICollisionGeometry<TQuery>
        where TSweep : struct, ICollisionSweep<TQuery, TGeometry>
    {
        private readonly CollisionBounds _bounds = GetBounds(in geometry, in pose, direction, length);
        public CollisionHit Hit;
        public bool Found;
        public bool Intersects(in CollisionBounds bounds) => _bounds.Overlaps(in bounds);

        public void Visit(scoped QueryRow row, scoped in TGeometry target, scoped in BodyPose targetPose)
        {
            float maximum = Found ? Hit.Distance : length;
            if (!sweep.Sweep(in geometry, in pose, in target, in targetPose, direction, maximum, out GeometryCastHit hit)) return;
            Hit = new(row.Entity, hit.Point, hit.Normal, hit.Distance, Transform(in targetPose), Version(row))
            {
                Feature = hit.Feature,
            };
            Found = true;
        }

        private static CollisionBounds GetBounds(in TQuery geometry, in BodyPose pose, Vector3 direction, float length)
        {
            BodyPose end = pose;
            end.Position += direction * length;
            CollisionBounds first = TQuery.Bounds(in geometry, in pose);
            CollisionBounds second = TQuery.Bounds(in geometry, in end);
            return CollisionBounds.Union(in first, in second);
        }
    }
}
