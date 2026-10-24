using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Animation.Tests;

public sealed class GeneratedChannelTests
{
    [Fact]
    public void GeneratedOrdinaryFieldReachesThePlaybackTarget()
    {
        using var world = new World();
        Entity target = world.CreateEntity(new FieldPanel { Style = new PanelStyle() });
        using var systems = Systems(world);
        PropertyAnimation.To(world, target, FieldPanelChannels.Opacity, 1f, Spec.Tween(1));

        var frame = new ImmediateSystemContext(world, 0, 0, 1);
        systems.Update(ref frame);

        Assert.Equal(1f, world.Read<FieldPanel>(target).Opacity);
    }

    [Fact]
    public void GeneratedValuePathWritesBackItsIntermediateProperty()
    {
        using var world = new World();
        Entity target = world.CreateEntity(new FieldPanel { Style = new PanelStyle() });
        using var systems = Systems(world);
        PropertyAnimation.To(world, target, FieldPanelChannels.InnerWidth, 12f, Spec.Tween(1));

        var frame = new ImmediateSystemContext(world, 0, 0, 1);
        systems.Update(ref frame);

        Assert.Equal(12f, world.Read<FieldPanel>(target).Inner.Width);
    }

    [Fact]
    public void GeneratedReferencePathWritesTheCurrentObjectAndRootVersion()
    {
        using var world = new World();
        var style = new PanelStyle();
        Entity target = world.CreateEntity(new FieldPanel { Style = style });
        using var systems = Systems(world);
        PropertyAnimation.To(world, target, FieldPanelChannels.Radius, 8f, Spec.Tween(1));
        uint version = world.WriteVersion<FieldPanel>(target);

        var frame = new ImmediateSystemContext(world, 0, 0, 1);
        systems.Update(ref frame);

        Assert.Equal(8f, style.Corner.Radius);
        Assert.NotEqual(version, world.WriteVersion<FieldPanel>(target));
    }

    [Fact]
    public void GeneratedWritablePropertyReachesThePlaybackTarget()
    {
        using var world = new World();
        Entity target = world.CreateEntity(new FieldPanel { Style = new PanelStyle() });
        using var systems = Systems(world);
        PropertyAnimation.To(world, target, FieldPanelChannels.Height, 24f, Spec.Tween(1));

        var frame = new ImmediateSystemContext(world, 0, 0, 1);
        systems.Update(ref frame);

        Assert.Equal(24f, world.Read<FieldPanel>(target).Height);
    }

    [Fact]
    public void HandWrittenAccessUsesTheGeneratedChannelPlayback()
    {
        using var world = new World();
        Entity target = world.CreateEntity(new FieldPanel { Style = new PanelStyle() });
        using var systems = Systems(world);
        PropertyAnimation.To(world, target, FieldPanelChannels.Custom, 32f, Spec.Tween(1));

        var frame = new ImmediateSystemContext(world, 0, 0, 1);
        systems.Update(ref frame);

        Assert.Equal(32f, world.Read<FieldPanel>(target).Style.Custom);
    }

    private static SystemGroup<ImmediateSystemContext> Systems(World world)
    {
        var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        systems.Add(new TimelineSystem());
        systems.Add(new TrackSystem<float, FloatOps>());
        systems.Add(new TimelineEndSystem());
        return systems;
    }
}

[GenerateFields]
[ComponentField("Inner.Width", Name = "InnerWidth")]
[ComponentField("Style.Corner.Radius", Name = "Radius")]
[ComponentField(typeof(PanelCustomAccess), "Custom")]
internal struct FieldPanel : IComponent
{
    public float Opacity;
    public PanelInner Inner { get; set; }
    public PanelStyle Style;
    public float Height { get; set; }
}

internal struct PanelInner
{
    public float Width { get; set; }
}

internal struct PanelCorner
{
    public float Radius { get; set; }
}

internal sealed class PanelStyle
{
    public PanelCorner Corner { get; set; }
    public float Custom;
}

internal readonly struct PanelCustomAccess : IFieldAccess<FieldPanel, float>
{
    public static float Read(in FieldPanel component) => component.Style.Custom;
    public static void Write(ref FieldPanel component, in float value) => component.Style.Custom = value;
}
