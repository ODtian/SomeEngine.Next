using System.Numerics;

namespace SomeEngine.Physics.Tests;

public sealed class BoxSideContactTests
{
    [Fact]
    public void TouchingBoxesKeepTheirSideContactAtThePyramidArrayCoordinates()
    {
        var box = new BoxGeometry(new(.5f));
        var first = new BodyPose
        {
            Position = new(-229.49994f, -53.999992f, 15.494312f),
            Rotation = new(-2.0194131e-5f, 1.1930933e-5f, -9.0576505e-6f, 1),
        };
        var second = new BodyPose
        {
            Position = new(-228.49994f, -54, 15.494275f),
            Rotation = new(-1.0873484e-5f, 1.927164e-6f, -6.002229e-6f, 1),
        };
        var collision = new ConvexCollision<BoxGeometry, BoxGeometry>(new(1e-5f, 32, 64));

        CollisionDistance distance = collision.Distance(in box, in first, in box, in second);
        Span<CollisionDistance> contacts = stackalloc CollisionDistance[4];
        int count = collision.GetManifold(in box, in first, in box, in second, .02f, contacts);

        Assert.InRange(Vector3.Distance(Vector3.UnitX, distance.Normal), 0, 1e-3f);
        Assert.InRange(distance.Separation, -.0001f, .0001f);
        Assert.Equal(4, count);
        foreach (CollisionDistance contact in contacts[..count])
        {
            Assert.InRange(Vector3.Distance(Vector3.UnitX, contact.Normal), 0, 1e-3f);
            Assert.InRange(contact.Separation, -.0001f, .0001f);
        }
    }
}
