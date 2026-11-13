using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.Physics;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed partial class DeviceAvbdTests
{
    [Fact]
    public void DeviceContactKeepsItsFeaturesAndResidentForceAfterBothBodiesTranslate()
    {
        using var scene = new HistoryContactScene(this, false);
        scene.Step(0, true);
        var original = scene.PointKey();
        scene.Translate(new Vector3(0, .1f, 0));
        scene.Step(1, true);
        Assert.Equal(original, scene.PointKey());
        Assert.InRange(scene.World.ReadComponentData<BodyPose>(scene.Moving).Position.Y, .09999f, .10001f);
        Assert.InRange(scene.World.ReadComponentData<BodyPose>(scene.Moving).Position.X, 1.94443f, 1.94446f);
        Assert.InRange(scene.World.ReadComponentData<BodyVelocity>(scene.Moving).Linear.X, 2.7776f, 2.7780f);
        Assert.InRange(scene.ContactForce.X, 11.110f, 11.113f);
        Assert.Equal(1, scene.World.ReadComponentData<BodyStepState>(scene.Moving).Number);
    }
}
