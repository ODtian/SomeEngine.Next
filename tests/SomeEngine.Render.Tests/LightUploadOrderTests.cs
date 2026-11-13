using System.Numerics;
using SomeEngine.Render.Components;
using SomeEngine.Render.Lighting;
using SomeEngine.Render.Systems;

namespace SomeEngine.Render.Tests;

public sealed class LightUploadOrderTests
{
    [Fact]
    public void PointAndSpotLightsShareIntensityAndDistancePriority()
    {
        var lights = new RenderLightSet();
        lights.AddPoint(new RenderPointLight(Vector3.UnitZ * 2, 10, Vector3.One, 4, uint.MaxValue));
        lights.AddPoint(new RenderPointLight(Vector3.UnitZ * 4, 10, Vector3.One, 64, uint.MaxValue));
        lights.AddSpot(new RenderSpotLight(Vector3.UnitZ, 10, -Vector3.UnitZ, 0.9f, 0.7f,
            Vector3.One, 2, uint.MaxValue));

        IReadOnlyList<RenderLightIndex> order = lights.OrderForUpload(Vector3.Zero);

        Assert.Equal([new(RenderLightKind.Point, 1), new(RenderLightKind.Spot, 0),
            new(RenderLightKind.Point, 0)], order);
    }

    [Fact]
    public void EntityIdsResolveEqualPrioritiesRegardlessOfSubmissionOrder()
    {
        using var world = new RenderWorld();
        var first = world.CreateEntity();
        var second = world.CreateEntity();
        var lights = new RenderLightSet();
        lights.AddSpot(second, new RenderSpotLight(Vector3.UnitZ, 10, -Vector3.UnitZ, 0.9f, 0.7f,
            Vector3.One, 1, uint.MaxValue));
        lights.AddPoint(first, new RenderPointLight(Vector3.UnitZ, 10, Vector3.One, 1, uint.MaxValue));

        IReadOnlyList<RenderLightIndex> order = lights.OrderForUpload(Vector3.Zero);

        Assert.Equal([new(RenderLightKind.Point, 0), new(RenderLightKind.Spot, 0)], order);
    }

    [Fact]
    public void CameraMovementChangesUploadPriority()
    {
        var lights = new RenderLightSet();
        lights.AddPoint(new RenderPointLight(Vector3.UnitX, 10, Vector3.One, 1, uint.MaxValue));
        lights.AddPoint(new RenderPointLight(Vector3.UnitX * 9, 10, Vector3.One, 1, uint.MaxValue));
        Assert.Equal(new(RenderLightKind.Point, 0), lights.OrderForUpload(Vector3.Zero)[0]);

        Assert.Equal(new(RenderLightKind.Point, 1), lights.OrderForUpload(Vector3.UnitX * 10)[0]);
    }
}
