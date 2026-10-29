using System.Numerics;

namespace SomeEngine.Physics;

public interface ICollisionSweep<TFirst, TSecond> where TFirst : struct where TSecond : struct
{
    bool Sweep(in TFirst first, in BodyPose firstPose, in TSecond second, in BodyPose secondPose,
        Vector3 direction, float length, out GeometryCastHit hit);
}

/// <summary>Translates a convex query shape to the successive separating planes supplied by its distance kernel.</summary>
public readonly struct ConvexShapeSweep<TFirst, TSecond, TDistance>(TDistance distance, CollisionQuerySettings settings)
    : ICollisionSweep<TFirst, TSecond>
    where TFirst : struct
    where TSecond : struct
    where TDistance : struct, ICollisionNarrowPhase<TFirst, TSecond>
{
    public bool Sweep(in TFirst first, in BodyPose firstPose, in TSecond second, in BodyPose secondPose,
        Vector3 direction, float length, out GeometryCastHit hit)
    {
        float traveled = 0;
        BodyPose pose = firstPose;
        for (int iteration = 0; iteration < settings.Iterations; iteration++)
        {
            pose.Position = firstPose.Position + direction * traveled;
            CollisionDistance contact = distance.Distance(in first, in pose, in second, in secondPose);
            if (contact.Separation <= settings.Tolerance)
            {
                hit = new(traveled, contact.SecondPoint, -contact.Normal, contact.SecondFeature);
                return true;
            }
            float closing = Vector3.Dot(direction, contact.Normal);
            if (closing <= 0) break;
            traveled += contact.Separation / closing;
            if (traveled > length) break;
        }
        hit = default;
        return false;
    }
}
