using System.Numerics;

namespace SomeEngine.Physics;

public readonly record struct SdfSurfaceSampling(int XCells, int YCells, int ZCells, int RootIterations, float Tolerance);

internal interface ISdfSurfaceOutput
{
    void Add(in CollisionFeaturePoint point);
}

internal static class SdfSurfacePoints
{
    internal static void Read<TField, TOutput>(in SdfGeometry<TField> geometry, in BodyPose pose,
        in SdfSurfaceSampling sampling, ref TOutput output)
        where TField : struct, ISdfField<TField>
        where TOutput : struct, ISdfSurfaceOutput, allows ref struct
    {
        CollisionBounds bounds = TField.Bounds(geometry.Field);
        Vector3 step = (bounds.Max - bounds.Min) / new Vector3(sampling.XCells, sampling.YCells, sampling.ZCells);
        for (int z = 0; z <= sampling.ZCells; z++)
        for (int y = 0; y <= sampling.YCells; y++)
        for (int x = 0; x <= sampling.XCells; x++)
        {
            Vector3 first = bounds.Min + step * new Vector3(x, y, z);
            SdfSample sample = TField.Sample(geometry.Field, first);
            if (x < sampling.XCells)
                ReadEdge(in geometry, in pose, in sampling, first, first + new Vector3(step.X, 0, 0), sample, ref output);
            if (y < sampling.YCells)
                ReadEdge(in geometry, in pose, in sampling, first, first + new Vector3(0, step.Y, 0), sample, ref output);
            if (z < sampling.ZCells)
                ReadEdge(in geometry, in pose, in sampling, first, first + new Vector3(0, 0, step.Z), sample, ref output);
        }
    }

    private static void ReadEdge<TField, TOutput>(in SdfGeometry<TField> geometry, in BodyPose pose,
        in SdfSurfaceSampling sampling, Vector3 first, Vector3 second, SdfSample firstSample, ref TOutput output)
        where TField : struct, ISdfField<TField>
        where TOutput : struct, ISdfSurfaceOutput, allows ref struct
    {
        SdfSample secondSample = TField.Sample(geometry.Field, second);
        if (MathF.Abs(firstSample.Distance) <= sampling.Tolerance)
        {
            var point = new CollisionFeaturePoint(GeometrySpace.ToWorld(in pose, first * geometry.Scale), firstSample.Feature);
            output.Add(in point);
        }
        if (MathF.Abs(secondSample.Distance) <= sampling.Tolerance)
        {
            var point = new CollisionFeaturePoint(GeometrySpace.ToWorld(in pose, second * geometry.Scale), secondSample.Feature);
            output.Add(in point);
        }
        if (firstSample.Distance * secondSample.Distance >= 0) return;
        Vector3 root = (first + second) * .5f;
        SdfSample sample = default;
        for (int iteration = 0; iteration < sampling.RootIterations; iteration++)
        {
            root = (first + second) * .5f;
            sample = TField.Sample(geometry.Field, root);
            if (MathF.Abs(sample.Distance) <= sampling.Tolerance) break;
            if ((sample.Distance < 0) == (firstSample.Distance < 0))
            {
                first = root;
                firstSample = sample;
            }
            else second = root;
        }
        var contact = new CollisionFeaturePoint(GeometrySpace.ToWorld(in pose, root * geometry.Scale), sample.Feature);
        output.Add(in contact);
    }
}

internal ref struct SdfContactSet
{
    private readonly Span<CollisionDistance> _points;
    private readonly float _contactDistance;
    private readonly float _tolerance;
    internal int Count;
    internal CollisionDistance Best;

    internal SdfContactSet(Span<CollisionDistance> points, float contactDistance, float tolerance)
    {
        _points = points;
        _contactDistance = contactDistance;
        _tolerance = tolerance;
        Count = 0;
        Best = new(default, default, Vector3.UnitX, float.PositiveInfinity, 0, 0);
    }

    internal void Add(in CollisionDistance candidate)
    {
        if (candidate.Separation < Best.Separation) Best = candidate;
        if (_points.IsEmpty || candidate.Separation > _contactDistance + _tolerance) return;
        for (int index = 0; index < Count; index++)
        {
            if (Vector3.DistanceSquared(candidate.FirstPoint, _points[index].FirstPoint) > _tolerance * _tolerance
                || Vector3.DistanceSquared(candidate.SecondPoint, _points[index].SecondPoint) > _tolerance * _tolerance) continue;
            if (candidate.Separation < _points[index].Separation) _points[index] = candidate;
            return;
        }
        if (Count < 4) _points[Count++] = candidate;
        else
        {
            Span<CollisionDistance> candidates = stackalloc CollisionDistance[5];
            _points[..4].CopyTo(candidates);
            candidates[4] = candidate;
            CollisionManifolds.Reduce(candidates, _points);
        }
    }
}
