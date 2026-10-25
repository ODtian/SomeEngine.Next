using System.Numerics;

namespace SomeEngine.Physics;

public readonly record struct CollisionQuerySettings(float Tolerance, int Iterations);
public readonly record struct GeometryCastHit(float Distance, Vector3 Point, Vector3 Normal, int Feature);

public static class GeometryCasts
{
    /// <summary>Advances a point or sphere to successive separating planes of a convex geometry.</summary>
    public static bool Convex<TGeometry>(in TGeometry geometry, in BodyPose pose, in CollisionSphereSweep sweep,
        in CollisionQuerySettings settings, out GeometryCastHit hit)
        where TGeometry : struct, ICollisionGeometry<TGeometry>
    {
        float distance = 0;
        for (int iteration = 0; iteration < settings.Iterations; iteration++)
        {
            GeometryPointDistance surface = TGeometry.DistanceToPoint(in geometry, in pose,
                sweep.Center + sweep.Direction * distance);
            float gap = surface.Distance - sweep.Radius;
            if (gap <= settings.Tolerance)
            {
                hit = new(distance, surface.Point, surface.Normal, surface.Feature);
                return true;
            }
            float closing = -Vector3.Dot(sweep.Direction, surface.Normal);
            if (closing <= 0) break;
            distance += gap / closing;
            if (distance > sweep.Length) break;
        }
        hit = default;
        return false;
    }
}
