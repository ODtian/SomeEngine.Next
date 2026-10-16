using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using static SomeEngine.Animation.Tests.PropertyAnimationTests;

namespace SomeEngine.Animation.Tests;

public sealed class AnimationLayersTests
{
    private static readonly Channel<float> Value = Channel<float>.Of<Display>(
        static (ref Display display) => ref display.Value);
    private static readonly Channel<float> HoverWeight = Channel<float>.Of<AnimationLayers>(
        static (ref AnimationLayers layers) => ref layers.Items[1].Weight);
    private static readonly Channel<float> Other = Channel<float>.Of<Display>(
        static (ref Display display) => ref display.Other);
    private static readonly Channel<Vector2> Position = Channel<Vector2>.Of<Display>(
        static (ref Display display) => ref display.Position);

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void FinishedValueTracksContinueBlendingWithChangingLayerWeights(bool inline, bool buffered)
    {
        using var world = new World();
        Entity target = world.CreateEntity(new Display { Value = 10 });
        var layers = new AnimationLayers { Count = 2 };
        layers.Items[0] = new AnimationLayer { Weight = 1 };
        layers.Items[1] = new AnimationLayer { Mode = LayerMode.Additive, Weight = 0 };
        world.Add(target, layers);
        PropertyAnimation.SetBase(world, target, Value, 10f);
        if (inline)
            PropertyAnimation.Declare<float>(world, target);
        if (buffered)
            world.AddBuffer<Contribution<float>>(target);
        PropertyAnimation.To(world, target, Value, 16f, Spec.Tween(0.25f), layer: 1);
        PropertyAnimation.To(world, target, HoverWeight, 1f, Spec.Tween(1));
        using var systems = Systems(world);

        Tick(systems, world, 0.25);
        Assert.Equal(11.5f, world.Read<Display>(target).Value);
        Tick(systems, world, 0.25);
        Assert.Equal(13f, world.Read<Display>(target).Value);
        Tick(systems, world, 0.5);
        Assert.Equal(16f, world.Read<Display>(target).Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LayerWeightsCanStartChangingAfterTheValueTrackCompletes(bool inline)
    {
        using var world = new World();
        Entity target = world.CreateEntity(new Display { Value = 10 });
        var layers = new AnimationLayers { Count = 2 };
        layers.Items[0] = new AnimationLayer { Weight = 1 };
        layers.Items[1] = new AnimationLayer { Mode = LayerMode.Additive, Weight = 0.25f };
        world.Add(target, layers);
        PropertyAnimation.SetBase(world, target, Value, 10f);
        if (inline)
            PropertyAnimation.Declare<float>(world, target);
        PropertyAnimation.To(world, target, Value, 16f, Spec.Tween(0.25f), layer: 1);
        using var systems = Systems(world);
        Tick(systems, world, 0.25);
        Assert.Equal(11.5f, world.Read<Display>(target).Value);

        PropertyAnimation.To(world, target, HoverWeight, 1f, Spec.Tween(0.75f));

        Tick(systems, world, 0.25);
        Assert.Equal(13f, world.Read<Display>(target).Value);
        Tick(systems, world, 0.5);
        Assert.Equal(16f, world.Read<Display>(target).Value);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ToAfterCompletionStartsContinuouslyAtThePresentedValue(bool inline, bool buffered)
    {
        using var world = new World();
        Entity target = world.CreateEntity(new Display { Value = 10 });
        var layers = new AnimationLayers { Count = 1 };
        layers.Items[0] = new AnimationLayer { Mode = LayerMode.Additive, Weight = 1 };
        world.Add(target, layers);
        PropertyAnimation.SetBase(world, target, Value, 10f);
        if (inline)
            PropertyAnimation.Declare<float>(world, target);
        if (buffered)
            world.AddBuffer<Contribution<float>>(target);
        PropertyAnimation.To(world, target, Value, 16f, Spec.Tween(1));
        using var systems = Systems(world);
        Tick(systems, world, 1);
        Assert.Equal(16f, world.Read<Display>(target).Value);

        PropertyAnimation.To(world, target, Value, 20f, Spec.Tween(1));

        Tick(systems, world, 0);
        Assert.Equal(16f, world.Read<Display>(target).Value);
        Tick(systems, world, 0.5);
        Assert.Equal(18f, world.Read<Display>(target).Value);
        Tick(systems, world, 0.5);
        Assert.Equal(20f, world.Read<Display>(target).Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ToAfterCompletionKeepsContinuityAtPartialLayerWeight(bool inline)
    {
        using var world = new World();
        Entity target = world.CreateEntity(new Display { Value = 10 });
        var layers = new AnimationLayers { Count = 1 };
        layers.Items[0] = new AnimationLayer { Mode = LayerMode.Additive, Weight = 0.5f };
        world.Add(target, layers);
        PropertyAnimation.SetBase(world, target, Value, 10f);
        if (inline)
            PropertyAnimation.Declare<float>(world, target);
        PropertyAnimation.To(world, target, Value, 16f, Spec.Tween(1));
        using var systems = Systems(world);
        Tick(systems, world, 1);
        Assert.Equal(13f, world.Read<Display>(target).Value);

        PropertyAnimation.To(world, target, Value, 20f, Spec.Tween(1));

        Tick(systems, world, 0);
        Assert.Equal(13f, world.Read<Display>(target).Value);
        Tick(systems, world, 0.5);
        Assert.Equal(14f, world.Read<Display>(target).Value);
        Tick(systems, world, 0.5);
        Assert.Equal(15f, world.Read<Display>(target).Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BaseHoverAndFlipLayersComposeTheSameField(bool inline)
    {
        using var world = new World();
        Entity target = world.CreateEntity(new Display { Value = 10 });
        var layers = new AnimationLayers { Count = 3 };
        layers.Items[0] = new AnimationLayer { Mode = LayerMode.Override, Weight = 1 };
        layers.Items[1] = new AnimationLayer { Mode = LayerMode.Additive, Weight = 0.5f };
        layers.Items[2] = new AnimationLayer { Mode = LayerMode.Override, Weight = 0.25f };
        world.Add(target, layers);
        PropertyAnimation.SetBase(world, target, Value, 10f);
        if (inline)
            PropertyAnimation.Declare<float>(world, target);
        PropertyAnimation.To(world, target, Value, 30f, Spec.Tween(1), layer: 0);
        PropertyAnimation.By(world, target, Value, 6f, Spec.Tween(1), layer: 1);
        PropertyAnimation.To(world, target, Value, 50f, Spec.Tween(1), layer: 2);
        using var systems = Systems(world);

        Tick(systems, world, 0.5);

        // The base tween presents 20; hover adds 1.5; FLIP blends 25% toward 30.
        Assert.Equal(23.625f, world.Read<Display>(target).Value);
    }

    [Fact]
    public void AnimatingLayerWeightFadesInItsAdditiveEffect()
    {
        using var world = new World();
        Entity target = world.CreateEntity(new Display { Value = 10 });
        var layers = new AnimationLayers { Count = 2 };
        layers.Items[0] = new AnimationLayer { Mode = LayerMode.Override, Weight = 1 };
        layers.Items[1] = new AnimationLayer { Mode = LayerMode.Additive, Weight = 0 };
        world.Add(target, layers);
        PropertyAnimation.SetBase(world, target, Value, 10f);
        PropertyAnimation.To(world, target, Value, 16f, Spec.Tween(1), layer: 1);
        PropertyAnimation.To(world, target, HoverWeight, 1f, Spec.Tween(1));
        using var systems = Systems(world);

        Tick(systems, world, 0.25);
        Assert.Equal(10.375f, world.Read<Display>(target).Value);
        Tick(systems, world, 0.25);
        Assert.Equal(11.5f, world.Read<Display>(target).Value);
        Tick(systems, world, 0.25);
        Assert.Equal(13.375f, world.Read<Display>(target).Value);
        Tick(systems, world, 0.25);
        Assert.Equal(16f, world.Read<Display>(target).Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OverlappingDisplacementsReturnToTheCallersBase(bool inline)
    {
        using var world = new World();
        Entity target = world.CreateEntity(new Display { Value = 10 });
        var layers = new AnimationLayers { Count = 1 };
        layers.Items[0] = new AnimationLayer { Mode = LayerMode.Additive, Weight = 1 };
        world.Add(target, layers);
        PropertyAnimation.SetBase(world, target, Value, 10f);
        if (inline)
            PropertyAnimation.Declare<float>(world, target);
        PropertyAnimation.By(world, target, Value, 4f, Spec.Tween(1));
        using var systems = Systems(world);
        Tick(systems, world, 0.25);
        Assert.Equal(13f, world.Read<Display>(target).Value);

        PropertyAnimation.By(world, target, Value, 6f, Spec.Tween(1));
        Tick(systems, world, 0.25);
        Assert.Equal(16.5f, world.Read<Display>(target).Value);
        Tick(systems, world, 0.5);
        Assert.Equal(11.5f, world.Read<Display>(target).Value);
        Tick(systems, world, 0.25);
        Assert.Equal(10f, world.Read<Display>(target).Value);
        Tick(systems, world, 1);
        Assert.Equal(10f, world.Read<Display>(target).Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ContributionSystemsComposeInSourceOrder(bool reverse)
    {
        using var world = new World();
        Entity target = world.CreateEntity(new Display { Value = 10 });
        var layers = new AnimationLayers { Count = 1 };
        layers.Items[0] = new AnimationLayer { Mode = LayerMode.Override, Weight = 0.5f };
        world.Add(target, layers);
        PropertyAnimation.SetBase(world, target, Value, 10f);
        world.AddBuffer<Contribution<float>>(target);
        Entity first = world.CreateEntity();
        Entity second = world.CreateEntity();
        Entity third = world.CreateEntity();
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        var a = new FieldContributionSystem(target, new Contribution<float> { Channel = Value, Value = 20, Source = first });
        var b = new FieldContributionSystem(target, new Contribution<float> { Channel = Value, Value = 40, Source = second });
        var c = new FieldContributionSystem(target, new Contribution<float> { Channel = Value, Value = 60, Source = third });
        systems.Add(reverse ? c : a);
        systems.Add(b);
        systems.Add(reverse ? a : c);
        systems.Add(new ComposeSystem<float, FloatOps>());

        Tick(systems, world, 0);
        Assert.Equal(43.75f, world.Read<Display>(target).Value);
        Tick(systems, world, 0);
        Assert.Equal(43.75f, world.Read<Display>(target).Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ALayerMaskSelectsItsChannels(bool buffered)
    {
        using var world = new World();
        Entity target = world.CreateEntity(new Display { Value = 10, Other = 50 });
        var layers = new AnimationLayers { Count = 1 };
        layers.Items[0] = new AnimationLayer { Mode = LayerMode.Additive, Weight = 0.5f, Mask = new LayerMask(0, 1) };
        world.Add(target, layers);
        world.AddBuffer<LayerChannel>(target);
        world.ExecuteBufferWrite<LayerChannel>(target, static channels => channels.Add(LayerChannel.Of(Value)));
        PropertyAnimation.SetBase(world, target, Value, 10f);
        PropertyAnimation.SetBase(world, target, Other, 50f);
        if (buffered)
            world.AddBuffer<Contribution<float>>(target);
        PropertyAnimation.By(world, target, Value, 4f, Spec.Tween(1));
        PropertyAnimation.By(world, target, Other, 8f, Spec.Tween(1));
        using var systems = Systems(world);

        Tick(systems, world, 0.5);

        Assert.Equal(new Display { Value = 11, Other = 50 }, world.Read<Display>(target));
    }

    [Fact]
    public void DefaultSystemsBlendPropertyLayers()
    {
        using var world = new World();
        Entity target = world.CreateEntity(new Display { Value = 10 });
        var layers = new AnimationLayers { Count = 1 };
        layers.Items[0] = new AnimationLayer { Mode = LayerMode.Additive, Weight = 1 };
        world.Add(target, layers);
        PropertyAnimation.SetBase(world, target, Value, 10f);
        PropertyAnimation.By(world, target, Value, 4f, Spec.Tween(1));
        PropertyAnimation.By(world, target, Value, 6f, Spec.Tween(1));
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        AnimationSystems.AddDefaults(systems);

        Tick(systems, world, 0.5);

        Assert.Equal(15f, world.Read<Display>(target).Value);
    }

    [Fact]
    public void APausedLayerKeepsItsValueWhileAnotherLayerAdvances()
    {
        using var world = new World();
        Entity target = world.CreateEntity(new Display { Value = 10 });
        var layers = new AnimationLayers { Count = 2 };
        layers.Items[0] = new AnimationLayer { Mode = LayerMode.Override, Weight = 1 };
        layers.Items[1] = new AnimationLayer { Mode = LayerMode.Additive, Weight = 1 };
        world.Add(target, layers);
        PropertyAnimation.SetBase(world, target, Value, 10f);
        PropertyAnimation.To(world, target, Value, 30f, Spec.Tween(2));
        Entity hover = PropertyAnimation.By(world, target, Value, 4f, Spec.Tween(2), layer: 1);
        using var systems = Systems(world);
        Tick(systems, world, 0.5);
        Assert.Equal(18f, world.Read<Display>(target).Value);

        PropertyAnimation.Pause(world, hover);
        Tick(systems, world, 0.5);
        Assert.Equal(23f, world.Read<Display>(target).Value);
        PropertyAnimation.Resume(world, hover);
        Tick(systems, world, 0.5);
        Assert.Equal(27f, world.Read<Display>(target).Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CrossTypeEvaluatorsUseTheCurrentLayerWeights(bool reverse)
    {
        using var world = new World();
        Entity target = world.CreateEntity(new Display { Position = new Vector2(10, 20) });
        var layers = new AnimationLayers { Count = 2 };
        layers.Items[0] = new AnimationLayer { Mode = LayerMode.Override, Weight = 1 };
        layers.Items[1] = new AnimationLayer { Mode = LayerMode.Additive, Weight = 0 };
        world.Add(target, layers);
        PropertyAnimation.SetBase(world, target, Position, new Vector2(10, 20));
        PropertyAnimation.To(world, target, Position, new Vector2(16, 32), Spec.Tween(1), layer: 1);
        PropertyAnimation.To(world, target, HoverWeight, 1f, Spec.Tween(1));
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        systems.Add(new TimelineSystem());
        systems.Add(new TrackSystem<float, FloatOps>(TrackFields.LayerWeights));
        systems.Add(new ComposeSystem<float, FloatOps>(TrackFields.LayerWeights));
        if (reverse)
        {
            systems.Add(new TrackSystem<Vector2, Vector2Ops>());
            systems.Add(new TrackSystem<float, FloatOps>());
        }
        else
        {
            systems.Add(new TrackSystem<float, FloatOps>());
            systems.Add(new TrackSystem<Vector2, Vector2Ops>());
        }
        systems.Add(new ComposeSystem<Vector2, Vector2Ops>());
        systems.Add(new ComposeSystem<float, FloatOps>());
        systems.Add(new TimelineEndSystem());

        Tick(systems, world, 0.25);
        Assert.Equal(new Vector2(10.375f, 20.75f), world.Read<Display>(target).Position);
        Tick(systems, world, 0.25);
        Assert.Equal(new Vector2(11.5f, 23f), world.Read<Display>(target).Position);
    }

    [Fact]
    public void DisplacementsFollowAnUpdatedBaseAndReturnToIt()
    {
        using var world = new World();
        Entity target = world.CreateEntity(new Display { Value = 10 });
        var layers = new AnimationLayers { Count = 1 };
        layers.Items[0] = new AnimationLayer { Mode = LayerMode.Additive, Weight = 1 };
        world.Add(target, layers);
        PropertyAnimation.SetBase(world, target, Value, 10f);
        PropertyAnimation.By(world, target, Value, 4f, Spec.Tween(1));
        using var systems = Systems(world);
        Tick(systems, world, 0.25);
        Assert.Equal(13f, world.Read<Display>(target).Value);

        PropertyAnimation.SetBase(world, target, Value, 20f);
        PropertyAnimation.By(world, target, Value, 6f, Spec.Tween(1));
        Tick(systems, world, 0.25);
        Assert.Equal(26.5f, world.Read<Display>(target).Value);
        Tick(systems, world, 0.5);
        Assert.Equal(21.5f, world.Read<Display>(target).Value);
        Tick(systems, world, 0.25);
        Assert.Equal(20f, world.Read<Display>(target).Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RetargetingOneLayerKeepsTheOtherLayersIndependent(bool repeatTo)
    {
        using var world = new World();
        Entity target = world.CreateEntity(new Display { Value = 10 });
        var layers = new AnimationLayers { Count = 2 };
        layers.Items[0] = new AnimationLayer { Mode = LayerMode.Override, Weight = 1 };
        layers.Items[1] = new AnimationLayer { Mode = LayerMode.Additive, Weight = 1 };
        world.Add(target, layers);
        PropertyAnimation.SetBase(world, target, Value, 10f);
        PropertyAnimation.To(world, target, Value, 30f, Spec.Tween(2));
        Entity hover = PropertyAnimation.To(world, target, Value, 16f, Spec.Tween(2), layer: 1);
        using var systems = Systems(world);
        Tick(systems, world, 0.5);
        Assert.Equal(16.5f, world.Read<Display>(target).Value);

        if (repeatTo)
            PropertyAnimation.To(world, target, Value, 18f, Spec.Tween(2), layer: 1);
        else
            PropertyAnimation.Retarget(world, hover, Value, 18f);
        Tick(systems, world, 0.5);

        Assert.Equal(23.125f, world.Read<Display>(target).Value);
    }

    [Fact]
    public void ASingleSourceWritesThroughItsDeclaredTrackStorage()
    {
        using var world = new World();
        Entity target = world.CreateEntity(new Display { Value = 10 });
        PropertyAnimation.Declare<float>(world, target);
        PropertyAnimation.To(world, target, Value, 20f, Spec.Tween(1));
        using var systems = Systems(world);

        Tick(systems, world, 0.5);

        Assert.Equal(15f, world.Read<Display>(target).Value);
        Assert.False(world.HasBuffer<Contribution<float>>(target));
    }

    [Fact]
    public void UserValueOperationsBlendThroughTheSamePublicSystems()
    {
        using var world = new World();
        Channel<Meter> channel = Channel<Meter>.Of<Reading>(static (ref Reading reading) => ref reading.Value);
        Entity target = world.CreateEntity(new Reading { Value = new Meter(10) });
        var layers = new AnimationLayers { Count = 1 };
        layers.Items[0] = new AnimationLayer { Mode = LayerMode.Additive, Weight = 1 };
        world.Add(target, layers);
        PropertyAnimation.SetBase(world, target, channel, new Meter(10));
        PropertyAnimation.By(world, target, channel, new Meter(4), Spec.Tween(1));
        PropertyAnimation.By(world, target, channel, new Meter(6), Spec.Tween(1));
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        systems.Add(new TimelineSystem());
        systems.Add(new TrackSystem<Meter, MeterOps>());
        systems.Add(new ComposeSystem<Meter, MeterOps>());
        systems.Add(new TimelineEndSystem());

        Tick(systems, world, 0.5);
        Assert.Equal(new Meter(15), world.Read<Reading>(target).Value);
        Tick(systems, world, 0.5);
        Assert.Equal(new Meter(10), world.Read<Reading>(target).Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WeightContributionsAreBlendedBeforePropertyValues(bool reverse)
    {
        using var world = new World();
        Entity target = world.CreateEntity(new Display { Value = 10 });
        var layers = new AnimationLayers { Count = 2 };
        layers.Items[0] = new AnimationLayer { Mode = LayerMode.Override, Weight = 1 };
        layers.Items[1] = new AnimationLayer { Mode = LayerMode.Additive, Weight = 0 };
        world.Add(target, layers);
        PropertyAnimation.SetBase(world, target, Value, 10f);
        world.AddBuffer<Contribution<float>>(target);
        PropertyAnimation.To(world, target, Value, 16f, Spec.Tween(1), layer: 1);
        Entity first = world.CreateEntity();
        Entity second = world.CreateEntity();
        var a = new FieldContributionSystem(target, new Contribution<float> { Channel = HoverWeight, Value = 1, Source = first });
        var b = new FieldContributionSystem(target, new Contribution<float> { Channel = HoverWeight, Value = 0.5f, Source = second });
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        systems.Add(new TimelineSystem());
        systems.Add(reverse ? b : a);
        systems.Add(reverse ? a : b);
        systems.Add(new ComposeSystem<float, FloatOps>(TrackFields.LayerWeights));
        systems.Add(new TrackSystem<float, FloatOps>());
        systems.Add(new ComposeSystem<float, FloatOps>());
        systems.Add(new TimelineEndSystem());

        Tick(systems, world, 0.5);
        Assert.Equal(11.5f, world.Read<Display>(target).Value);
        Tick(systems, world, 0.5);
        Assert.Equal(13f, world.Read<Display>(target).Value);
    }

    [Theory]
    [InlineData(LayerMode.Override, false, 0.15, 7.5f)]
    [InlineData(LayerMode.Override, false, 0.25, 10f)]
    [InlineData(LayerMode.Override, true, 0.15, 7.5f)]
    [InlineData(LayerMode.Override, true, 0.25, 10f)]
    [InlineData(LayerMode.Additive, false, 0.15, 7.5f)]
    [InlineData(LayerMode.Additive, false, 0.25, 10f)]
    [InlineData(LayerMode.Additive, true, 0.15, 7.5f)]
    [InlineData(LayerMode.Additive, true, 0.25, 10f)]
    public void CrossingStagesBlendsTheLastReachedFieldValue(LayerMode mode, bool inline, double delta, float expected)
    {
        using var world = new World();
        Entity target = world.CreateEntity(new Display());
        var layers = new AnimationLayers { Count = 1 };
        layers.Items[0] = new AnimationLayer { Mode = mode, Weight = 0.5f };
        world.Add(target, layers);
        PropertyAnimation.SetBase(world, target, Value, 0f);
        world.AddBuffer<Contribution<float>>(target);
        if (inline)
            PropertyAnimation.Declare<float>(world, target);
        Entity animation = PropertyAnimation.To(world, target, Value, 10f, Spec.Tween(0.1f));
        AddDurationStages(world, animation);
        var next = new Track<float>
        {
            Target = inline ? Entity.Null : target,
            Channel = Value, From = 10, Current = 10, To = 20,
            Duration = 0.1f, Curve = Spec.Tween(0.1f).Curve, Stage = 1,
            StartId = world.Read<Timeline>(animation).Starts,
        };
        world.ExecuteBufferWrite<Track<float>, Track<float>>(animation, ref next,
            static (DynamicBuffer<Track<float>> tracks, ref Track<float> track) => tracks.Add(track));
        using var systems = Systems(world);

        Tick(systems, world, delta);
        Assert.Equal(expected, world.Read<Display>(target).Value, 5);
        Tick(systems, world, 0.25);
        Assert.Equal(10f, world.Read<Display>(target).Value);
        Assert.False(world.IsEnabled<Timeline>(animation));
        Tick(systems, world, 0.25);
        Assert.Equal(10f, world.Read<Display>(target).Value);
    }

    [Theory]
    [InlineData(0.15, 7.5f)]
    [InlineData(0.25, 0f)]
    public void CrossingStagesUsesTheLastReachedLayerWeight(double delta, float expected)
    {
        using var world = new World();
        Entity target = world.CreateEntity(new Display());
        var layers = new AnimationLayers { Count = 2 };
        layers.Items[0] = new AnimationLayer { Mode = LayerMode.Override, Weight = 1 };
        layers.Items[1] = new AnimationLayer { Mode = LayerMode.Additive, Weight = 0 };
        world.Add(target, layers);
        PropertyAnimation.SetBase(world, target, Value, 0f);
        PropertyAnimation.Declare<float>(world, target);
        world.AddBuffer<Contribution<float>>(target);
        PropertyAnimation.To(world, target, Value, 10f, Spec.Tween(0.1f), layer: 1);
        PropertyAnimation.To(world, target, HoverWeight, 1f, Spec.Tween(0.1f));
        AddDurationStages(world, target);
        uint starts = world.Read<Timeline>(target).Starts;
        world.ExecuteBufferWrite<Track<float>, uint>(target, ref starts,
            static (DynamicBuffer<Track<float>> tracks, ref uint starts) =>
            {
                tracks.Add(new Track<float>
                {
                    Channel = Value, From = 10, Current = 10, To = 20, Layer = 1,
                    Duration = 0.1f, Curve = Spec.Tween(0.1f).Curve, Stage = 1, StartId = starts,
                });
                tracks.Add(new Track<float>
                {
                    Channel = HoverWeight, From = 1, Current = 1, To = 0,
                    Duration = 0.1f, Curve = Spec.Tween(0.1f).Curve, Stage = 1, StartId = starts,
                });
            });
        using var systems = Systems(world);

        Tick(systems, world, delta);

        Assert.Equal(expected, world.Read<Display>(target).Value, 5);
    }

    private static void AddDurationStages(World world, Entity animation)
    {
        if (!world.HasBuffer<TimelineStage>(animation)) world.AddBuffer<TimelineStage>(animation);
        world.ExecuteBufferWrite<TimelineStage>(animation, static stages =>
        {
            stages.Add(new TimelineStage { End = StageEnd.Duration, Duration = 0.1f });
            stages.Add(new TimelineStage { End = StageEnd.Duration, Duration = 0.1f });
        });
    }

#if DEBUG
    [Fact]
    public void SameLayerSourcesRequireDistinctIds()
    {
        using var world = new World();
        Entity target = world.CreateEntity(new Display());
        world.AddBuffer<Contribution<float>>(target);
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        systems.Add(new FieldContributionSystem(target, new Contribution<float> { Channel = Value, Value = 20 }));
        systems.Add(new FieldContributionSystem(target, new Contribution<float> { Channel = Value, Value = 30 }));
        systems.Add(new ComposeSystem<float, FloatOps>());

        Assert.Throws<InvalidOperationException>(() => Tick(systems, world, 0));
    }
#endif

    public struct FieldContributionSystem(Entity target, Contribution<float> contribution) : ISystem<ImmediateSystemContext>
    {
        public void OnUpdate(ref ImmediateSystemContext context)
        {
            Contribution<float> value = contribution;
            context.World.ExecuteBufferWrite<Contribution<float>, Contribution<float>>(target, ref value,
                static (DynamicBuffer<Contribution<float>> values, ref Contribution<float> value) => values.Add(value));
        }
    }

    private static SystemGroup<ImmediateSystemContext> Systems(World world)
    {
        var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        systems.Add(new TimelineSystem());
        systems.Add(new TrackSystem<float, FloatOps>(TrackFields.LayerWeights));
        systems.Add(new ComposeSystem<float, FloatOps>(TrackFields.LayerWeights));
        systems.Add(new TrackSystem<float, FloatOps>());
        systems.Add(new ComposeSystem<float, FloatOps>());
        systems.Add(new TimelineEndSystem());
        return systems;
    }

    private static void Tick(SystemGroup<ImmediateSystemContext> systems, World world, double delta)
    {
        var frame = new ImmediateSystemContext(world, 0, 0, delta);
        systems.Update(ref frame);
    }

    public struct Display : IComponent
    {
        public float Value;
        public float Other;
        public Vector2 Position;
    }
}
