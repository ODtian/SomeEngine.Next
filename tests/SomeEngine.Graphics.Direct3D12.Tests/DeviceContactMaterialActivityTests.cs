using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;
using SomeEngine.Physics;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed partial class DeviceXpbdTests
{
    [Fact]
    public void EditingADeviceContactMaterialWakesAndReleasesItsSleepingBody()
    {
        using var scene = new SleepingContactScene(this);
        scene.Step(); scene.Step();
        Assert.True(scene.World.ReadComponentData<BodyActivity>(scene.Moving).Sleeping);

        scene.Soften(); scene.Step();

        Near(new(0, .9f, 0), scene.World.ReadComponentData<BodyPose>(scene.Moving).Position);
        Near(new(0, -1, 0), scene.World.ReadComponentData<BodyVelocity>(scene.Moving).Linear);
        Assert.False(scene.World.ReadComponentData<BodyActivity>(scene.Moving).Sleeping);
        var wake = Assert.Single(scene.Received, item => item.Event.Kind == PhysicsEventKind.Wake);
        Assert.Equal(scene.Moving, wake.Event.Subject); Assert.Equal(2, wake.Number); Assert.Equal(.2, wake.Event.Time);
    }

}
