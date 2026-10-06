using SomeEngine.Render.Shadows;
using SomeEngine.ECS;
using SomeEngine.Render.Components;
using SomeEngine.Render.Lighting;
using SomeEngine.Render.Systems;
using System.Numerics;

namespace SomeEngine.Render.Tests;

public sealed class ShadowWorkBudgetTests
{
    [Fact]
    public void DirectLightUpdatesKeepTheirEntityWhenInputOrderChanges()
    {
        using var world = new RenderWorld();
        var first = world.CreateEntity();
        var second = world.CreateEntity();
        var lights = new RenderLightSet();
        var point = new RenderPointLight(Vector3.Zero, 10f, Vector3.One, 1f, uint.MaxValue);
        lights.AddPoint(first, point);
        lights.AddPoint(second, point with { Position = Vector3.UnitX });
        lights.AddPoint(second, point with { Intensity = 3f });
        lights.AddPoint(first, point with { Intensity = 2f });
        Assert.Equal(first, lights.GetPointEntity(0));
        Assert.Equal(second, lights.GetPointEntity(1));
        Assert.Equal(2f, lights.Points[0].Intensity);
        Assert.Equal(3f, lights.Points[1].Intensity);
    }

    [Fact]
    public void TimeBudgetEnablesWorkFeedbackWithTheDefaultLoadBudget()
    {
        var defaults = new VirtualShadowMapSettings();
        Assert.Equal(0f, defaults.WorkLoadBudget);
        Assert.Equal(0f, defaults.WorkTimeBudgetMilliseconds);
        Assert.True((defaults with { WorkTimeBudgetMilliseconds = 1f }).WorkThrottlingEnabled);
    }
}
