using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Animation.Tests;

public sealed class TypedCompositionTests
{
    [Fact]
    public void DeclaredLayersComposeGeneratedReferenceOutputsInLayerOrder()
    {
        using var world = new World();
        var style = new PanelStyle();
        Entity target = world.CreateEntity(new FieldPanel { Style = style });
        var layers = new AnimationLayers { Count = 2 };
        layers.Items[0] = new() { Weight = 1 };
        layers.Items[1] = new() { Mode = LayerMode.Additive, Weight = 0.5f };
        world.Add(target, layers);
        var spec = Spec.Tween<float, FloatOps, Linear>(1);
        PropertyAnimation.Declare(world, target, FieldPanelChannels.Radius, spec, layer: 1);
        PropertyAnimation.Declare(world, target, FieldPanelChannels.Radius, spec, layer: 0);
        PropertyAnimation.SetBase(world, target, FieldPanelChannels.Radius, 0f);
        using var systems = Systems(world);
        PropertyAnimation.To(world, target, FieldPanelChannels.Radius, 4f, spec, layer: 1);
        PropertyAnimation.To(world, target, FieldPanelChannels.Radius, 10f, spec, layer: 0);

        Tick(systems, world, 1);

        Assert.Equal(12f, style.Corner.Radius);
    }

    [Fact]
    public void CompletedSourceRespondsToLayerWeightChanges()
    {
        using var world = new World();
        Entity target = world.CreateEntity(new FieldPanel());
        var layers = new AnimationLayers { Count = 1 };
        layers.Items[0] = new() { Weight = 1 };
        world.Add(target, layers);
        var spec = Spec.Tween<float, FloatOps, Linear>(1);
        PropertyAnimation.Declare(world, target, FieldPanelChannels.Opacity, spec);
        PropertyAnimation.SetBase(world, target, FieldPanelChannels.Opacity, 0f);
        using var systems = Systems(world);
        PropertyAnimation.To(world, target, FieldPanelChannels.Opacity, 10f, spec);
        Tick(systems, world, 1);

        layers.Items[0].Weight = 0.5f;
        world.Replace(target, layers);
        Tick(systems, world, 0);

        Assert.Equal(5f, world.Read<FieldPanel>(target).Opacity);
    }

    [Fact]
    public void UnchangedHeldOutputPreservesThePresentedComponentVersion()
    {
        using var world = new World();
        Entity target = world.CreateEntity(new FieldPanel());
        var layers = new AnimationLayers { Count = 1 };
        layers.Items[0] = new() { Weight = 1 };
        world.Add(target, layers);
        var spec = Spec.Tween<float, FloatOps, Linear>(1);
        PropertyAnimation.Declare(world, target, FieldPanelChannels.Opacity, spec);
        PropertyAnimation.SetBase(world, target, FieldPanelChannels.Opacity, 0f);
        using var systems = Systems(world);
        PropertyAnimation.To(world, target, FieldPanelChannels.Opacity, 10f, spec);
        Tick(systems, world, 1);
        uint version = world.WriteVersion<FieldPanel>(target);

        Tick(systems, world, 0.25);

        Assert.Equal(version, world.WriteVersion<FieldPanel>(target));
    }

    [Fact]
    public void CompletedDisplacementFollowsTheLatestBase()
    {
        using var world = new World();
        Entity target = world.CreateEntity(new FieldPanel { Opacity = 2 });
        var layers = new AnimationLayers { Count = 1 };
        layers.Items[0] = new() { Mode = LayerMode.Additive, Weight = 1 };
        world.Add(target, layers);
        var spec = Spec.Tween<float, FloatOps, Linear>(1);
        PropertyAnimation.Declare(world, target, FieldPanelChannels.Opacity, spec);
        PropertyAnimation.SetBase(world, target, FieldPanelChannels.Opacity, 2f);
        using var systems = Systems(world);
        PropertyAnimation.By(world, target, FieldPanelChannels.Opacity, 4f, spec);
        Tick(systems, world, 1);

        PropertyAnimation.SetBase(world, target, FieldPanelChannels.Opacity, 7f);
        Tick(systems, world, 0);

        Assert.Equal(7f, world.Read<FieldPanel>(target).Opacity);
    }

    [Fact]
    public void ExternalAndDeclaredOutputsUseTheSameSourceOrder()
    {
        using var world = new World();
        Entity target = world.CreateEntity(new FieldPanel());
        Entity external = world.CreateEntity();
        var layers = new AnimationLayers { Count = 1 };
        layers.Items[0] = new() { Weight = 0.5f };
        world.Add(target, layers);
        var spec = Spec.Tween<float, FloatOps, Linear>(1);
        PropertyAnimation.Declare(world, target, FieldPanelChannels.Opacity, spec);
        PropertyAnimation.DeclareContributions<float>(world, target);
        PropertyAnimation.SetBase(world, target, FieldPanelChannels.Opacity, 0f);
        using var systems = Systems(world);
        PropertyAnimation.To(world, target, FieldPanelChannels.Opacity, 10f, spec);
        PropertyAnimation.Contribute(world, target, new Contribution<float>
        {
            Channel = FieldPanelChannels.Opacity, Value = 20, Source = external,
        });

        Tick(systems, world, 1);

        Assert.Equal(12.5f, world.Read<FieldPanel>(target).Opacity);
    }

    [Fact]
    public void ChangingAHeldSourcesMaskRestoresTheBase()
    {
        using var world = new World();
        Entity target = world.CreateEntity(new FieldPanel());
        var layers = new AnimationLayers { Count = 1 };
        layers.Items[0] = new() { Weight = 1 };
        world.Add(target, layers);
        world.AddBuffer<LayerChannel>(target);
        var spec = Spec.Tween<float, FloatOps, Linear>(1);
        PropertyAnimation.Declare(world, target, FieldPanelChannels.Opacity, spec);
        PropertyAnimation.SetBase(world, target, FieldPanelChannels.Opacity, 2f);
        using var systems = Systems(world);
        PropertyAnimation.To(world, target, FieldPanelChannels.Opacity, 10f, spec);
        Tick(systems, world, 1);

        layers.Items[0].Mask = new LayerMask(0, 0);
        world.Replace(target, layers);
        Tick(systems, world, 0);

        Assert.Equal(2f, world.Read<FieldPanel>(target).Opacity);
    }

    private static SystemGroup<ImmediateSystemContext> Systems(World world)
    {
        var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        systems.Add(new TimelineSystem());
        systems.Add(new TypedTrackSystem<float, FloatOps, TweenParameters, TweenState, Tween<float, FloatOps, Linear>>());
        systems.Add(new ComposeSystem<float, FloatOps>());
        systems.Add(new TimelineEndSystem());
        return systems;
    }

    private static void Tick(SystemGroup<ImmediateSystemContext> systems, World world, double delta)
    {
        var frame = new ImmediateSystemContext(world, 0, 0, delta);
        systems.Update(ref frame);
    }
}
