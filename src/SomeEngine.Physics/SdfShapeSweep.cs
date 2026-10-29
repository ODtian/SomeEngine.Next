using System.Numerics;

namespace SomeEngine.Physics;

/// <summary>Advances a translated query shape using the selected field-distance kernel.</summary>
public readonly struct SdfShapeSweep<TGeometry, TField, TKernel>(TKernel kernel, CollisionQuerySettings settings)
    : ICollisionSweep<TGeometry, SdfGeometry<TField>>
    where TGeometry : struct
    where TField : struct, ISdfField<TField>
    where TKernel : struct, ICollisionNarrowPhase<TGeometry, SdfGeometry<TField>>
{
    public bool Sweep(in TGeometry first, in BodyPose firstPose, in SdfGeometry<TField> second,
        in BodyPose secondPose, Vector3 direction, float length, out GeometryCastHit hit)
    {
        float distance = 0;
        BodyPose current = firstPose;
        for (int iteration = 0; iteration < settings.Iterations; iteration++)
        {
            current.Position = firstPose.Position + direction * distance;
            CollisionDistance gap = kernel.Distance(in first, in current, in second, in secondPose);
            if (gap.Separation <= settings.Tolerance)
            {
                hit = new(distance, gap.SecondPoint, -gap.Normal, gap.SecondFeature);
                return true;
            }
            distance += gap.Separation * second.TraceStepScale;
            if (distance > length) break;
        }
        hit = default;
        return false;
    }
}
