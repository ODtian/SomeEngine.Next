using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Animation.Checks;

internal static unsafe partial class FullPathMeasurements
{
    private static void MeasureSources(int count)
    {
        MeasureFixedSources(count);
        MeasureDynamicSources(count);
        MeasureHeldSources(count);
    }

    private static AnimationLayers TwoLayers()
    {
        var layers = new AnimationLayers { Count = 2 };
        layers.Items[0] = new() { Mode = LayerMode.Override, Weight = 0.75f };
        layers.Items[1] = new() { Mode = LayerMode.Additive, Weight = 0.5f };
        return layers;
    }

    private static void MeasureFixedSources(int count)
    {
        using var world = new World(count);
        var channel = Channel<float>.Of<Plain, PlainAccess>(1, false, 0);
        var spec = Spec.Tween<float, FloatOps, Linear>(10_000);
        for (int item = 0; item < count; item++)
        {
            Entity target = world.CreateEntity(new Plain());
            world.Add(target, TwoLayers());
            PropertyAnimation.SetBase(world, target, channel, 0f);
            PropertyAnimation.Declare(world, target, channel, spec, layer: 0);
            PropertyAnimation.Declare(world, target, channel, spec, layer: 1);
            PropertyAnimation.To(world, target, channel, 10f, spec, layer: 0);
            PropertyAnimation.To(world, target, channel, 20f, spec, layer: 1);
        }
        using var systems = TweenSystems(world);
        var context = new ImmediateSystemContext(world, 0, 0, FrameDelta);
        Record("source", "fixed-two-per-field", count * 2, () => systems.Update(ref context));
    }

    private static void MeasureDynamicSources(int count)
    {
        using var world = new World(count);
        var channel = Channel<float>.Of<Plain, PlainAccess>(1, false, 0);
        var targets = new Entity[count];
        Entity first = world.CreateEntity();
        Entity second = world.CreateEntity();
        for (int item = 0; item < count; item++)
        {
            Entity target = targets[item] = world.CreateEntity(new Plain());
            world.Add(target, TwoLayers());
            PropertyAnimation.DeclareContributions<float>(world, target, 2);
            PropertyAnimation.SetBase(world, target, channel, 0f);
        }
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        systems.Add(new ComposeSystem<float, FloatOps>());
        systems.Initialize();
        var pair = new ContributionPair
        {
            First = new() { Source = first, Output = 1, Channel = channel, Layer = 0, Value = 10 },
            Second = new() { Source = second, Output = 1, Channel = channel, Layer = 1, Value = 20 },
        };
        Record("source", "dynamic-submit-sort-compose", count * 2, () =>
        {
            pair.Reverse = !pair.Reverse;
            foreach (Entity target in targets)
            {
                PropertyAnimation.Contribute(world, target, pair.Reverse ? pair.Second : pair.First);
                PropertyAnimation.Contribute(world, target, pair.Reverse ? pair.First : pair.Second);
            }
            systems.Update();
        });
        Require(channel.Read(world, targets[^1]) == 17.5f, "Dynamic sources must follow the declared layer formula.");
    }

    private static void MeasureHeldSources(int count)
    {
        using var world = new World(count);
        var channel = Channel<float>.Of<Plain, PlainAccess>(1, false, 0);
        var spec = Spec.Tween<float, FloatOps, Linear>(0.001f);
        var targets = new Entity[count];
        for (int item = 0; item < count; item++)
        {
            Entity target = targets[item] = world.CreateEntity(new Plain());
            world.Add(target, TwoLayers());
            PropertyAnimation.SetBase(world, target, channel, 0f);
            PropertyAnimation.Declare(world, target, channel, spec, layer: 0);
            PropertyAnimation.Declare(world, target, channel, spec, layer: 1);
            PropertyAnimation.To(world, target, channel, 10f, spec, layer: 0);
            PropertyAnimation.To(world, target, channel, 20f, spec, layer: 1);
        }
        using var systems = TweenSystems(world);
        var context = new ImmediateSystemContext(world, 0, 0, FrameDelta);
        systems.Update(ref context);
        Record("source", "held-unchanged", count * 2, () => systems.Update(ref context));
        float weight = 0.5f;
        Record("source", "held-layer-change", count * 2, () =>
        {
            weight = weight == 0.5f ? 0.75f : 0.5f;
            foreach (Entity target in targets)
            {
                AnimationLayers layers = world.Read<AnimationLayers>(target);
                layers.Items[1].Weight = weight;
                world.Replace(target, in layers);
            }
            systems.Update(ref context);
        });
        Require(channel.Read(world, targets[^1]) == 7.5f + 20 * weight,
            "Held sources must compose the current layer weights.");
        float basis = 0;
        Record("source", "held-base-change", count * 2, () =>
        {
            basis = basis == 0 ? 2 : 0;
            foreach (Entity target in targets) PropertyAnimation.SetBase(world, target, channel, basis);
            systems.Update(ref context);
        });
        Require(channel.Read(world, targets[^1]) == basis * 0.25f + 7.5f + (20 - basis) * weight,
            "Held sources must compose the current base value.");
    }

    private static SystemGroup<ImmediateSystemContext> TweenSystems(World world)
    {
        var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        var spec = Spec.Tween<float, FloatOps, Linear>(10_000);
        AnimationSystems.Add(systems, in spec);
        systems.Initialize();
        return systems;
    }

    private struct ContributionPair
    {
        public Contribution<float> First, Second;
        public bool Reverse;
    }
}
