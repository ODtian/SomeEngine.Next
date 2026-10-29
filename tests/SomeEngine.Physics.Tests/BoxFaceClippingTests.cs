using System.Numerics;

namespace SomeEngine.Physics.Tests;

public sealed class BoxFaceClippingTests
{
    [Fact]
    public void ExpandedBoxManifoldQueriesKeepTheirWitnessesWithinTheSampledFaces()
    {
        var box = new BoxGeometry(new(.2f));
        // The original Silo step 39 samples predicted rotations at the two start centers.
        var first = new BodyPose
        {
            Position = new(-8.274739f, 3.6800156f, 5.733334f),
            Rotation = new(-1.7734084e-6f, -4.2478885e-7f, -.84723353f, -.5312207f),
        };
        var second = new BodyPose
        {
            Position = new(-7.777169f, 4.0455575f, 5.733352f),
            Rotation = new(9.07699e-6f, 8.540063e-6f, .61708343f, .7868976f),
        };
        var collision = new ConvexCollision<BoxGeometry, BoxGeometry>(new(1e-5f, 32, 64));
        CollisionDistance nearest = collision.Distance(in box, in first, in box, in second);
        Span<CollisionDistance> points = stackalloc CollisionDistance[4];
        // Expanded distance queries use the same face clipping as predictive contact sampling.
        int count = collision.GetManifold(in box, in first, in box, in second, nearest.Separation + .02f, points);

        Assert.InRange(count, 1, 4);
        foreach (CollisionDistance point in points[..count])
        {
            Vector3 firstLocal = Vector3.Transform(point.FirstPoint - first.Position, Quaternion.Conjugate(first.Rotation));
            Vector3 secondLocal = Vector3.Transform(point.SecondPoint - second.Position, Quaternion.Conjugate(second.Rotation));
            for (int axis = 0; axis < 3; axis++)
            {
                Assert.InRange(firstLocal[axis], -.2001f, .2001f);
                Assert.InRange(secondLocal[axis], -.2001f, .2001f);
            }
            Assert.InRange(point.Separation, 0, nearest.Separation + .0201f);
        }
    }
}