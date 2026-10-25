namespace SomeEngine.Physics;

/// <summary>Uses the same concrete kernel with reversed participants and contact orientation.</summary>
public readonly struct ReverseCollision<TFirst, TSecond, TKernel>(TKernel kernel) : ICollisionNarrowPhase<TSecond, TFirst>
    where TFirst : struct
    where TSecond : struct
    where TKernel : struct, ICollisionNarrowPhase<TFirst, TSecond>
{
    public int ContactCapacity => kernel.ContactCapacity;
    public CollisionDistance Distance(in TSecond first, in BodyPose firstPose,
        in TFirst second, in BodyPose secondPose) =>
        kernel.Distance(in second, in secondPose, in first, in firstPose).Reverse();

    public int GetManifold(in TSecond first, in BodyPose firstPose, in TFirst second, in BodyPose secondPose,
        float contactDistance, Span<CollisionDistance> points)
    {
        int count = kernel.GetManifold(in second, in secondPose, in first, in firstPose, contactDistance, points);
        for (int index = 0; index < count; index++) points[index] = points[index].Reverse();
        return count;
    }
}
