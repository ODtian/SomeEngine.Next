using System.Numerics;

namespace SomeEngine.Physics.Tests;

public sealed class BoxFloorContactTests
{
    [Theory]
    [InlineData(-5f, -5f, .49722221f, -9.951337e-8f, 7.128587e-8f, -3.222449e-8f)]
    [InlineData(-1.7399466e-7f, 9.4729415e-9f, .4583333f, 4.053291e-9f, -1.8617355e-8f, -2.0969383e-8f)]
    public void ABoxTouchingAWideFloorKeepsItsVerticalContact(float x, float y, float z,
        float rotationX, float rotationY, float rotationZ)
    {
        var floor = new BoxGeometry(new(1250, 1250, .5f));
        var box = new BoxGeometry(new(.5f));
        var floorPose = new BodyPose { Position = new(0, 0, -.5f), Rotation = Quaternion.Identity };
        var boxPose = new BodyPose { Position = new(x, y, z), Rotation = new(rotationX, rotationY, rotationZ, 1) };
        var collision = new ConvexCollision<BoxGeometry, BoxGeometry>(new(1e-5f, 32, 64));

        CollisionDistance distance = collision.Distance(in floor, in floorPose, in box, in boxPose);
        Span<CollisionDistance> points = stackalloc CollisionDistance[4];
        int count = collision.GetManifold(in floor, in floorPose, in box, in boxPose, .02f, points);

        Assert.InRange(Vector3.Distance(Vector3.UnitZ, distance.Normal), 0, 1e-4f);
        Assert.InRange(distance.Separation, z - .50002f, z - .49998f);
        Assert.Equal(4, count);
        foreach (CollisionDistance point in points[..count])
        {
            Assert.InRange(Vector3.Distance(Vector3.UnitZ, point.Normal), 0, 1e-4f);
            Assert.InRange(Vector3.Distance(boxPose.Position, point.SecondPoint), 0, 1);
        }
    }
}
