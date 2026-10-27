using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Animation.Tests;

public sealed class AnimationPhaseTests
{
    private static readonly Channel<float> Value = Channel<float>.Of<Display>(static (ref Display display) => ref display.Value);
    private static readonly Channel<float> Weight = Channel<float>.Of<AnimationLayers>(static (ref AnimationLayers layers) => ref layers.Items[0].Weight);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IdleDefaultStagesRecomposeHeldSourcesWhenTheirLayerChanges(bool jobs)
    {
        using var world = new World(execution: jobs ? new WorldJobs(JobSystem.Default) : null);
        Entity target = world.CreateEntity(new Display());
        var layers = new AnimationLayers { Count = 1 };
        layers.Items[0].Weight = 1;
        world.Add(target, layers);
        PropertyAnimation.SetBase(world, target, Value, 0f);
        PropertyAnimation.To(world, target, Value, 10f, Spec.Tween(0.1f));
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        AnimationSystems.AddDefaults(systems);
        var context = new ImmediateSystemContext(world, 0, 0, 0.25);
        systems.Update(ref context);
        systems.Update(ref context);
        uint version = world.WriteVersion<Display>(target);
        systems.Update(ref context);
        Assert.Equal(version, world.WriteVersion<Display>(target));

        layers.Items[0].Weight = 0.5f;
        world.Replace(target, layers);
        systems.Update(ref context);

        Assert.Equal(5f, world.Read<Display>(target).Value);
    }

    [Fact]
    public void AFrameContributionRestoresHeldSourcesOnTheFollowingIdleFrame()
    {
        using var world = new World();
        Entity target = world.CreateEntity(new Display());
        Entity external = world.CreateEntity();
        var layers = new AnimationLayers { Count = 1 };
        layers.Items[0].Weight = 0.5f;
        world.Add(target, layers);
        PropertyAnimation.SetBase(world, target, Value, 0f);
        var spec = Spec.Tween<float, FloatOps, Linear>(0.1f);
        PropertyAnimation.Declare(world, target, Value, spec);
        PropertyAnimation.To(world, target, Value, 10f, spec);
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        AnimationSystems.AddDefaults(systems);
        var context = new ImmediateSystemContext(world, 0, 0, 0.25);
        systems.Update(ref context);
        systems.Update(ref context);
        systems.Update(ref context);

        PropertyAnimation.Contribute(world, target, new Contribution<float> { Channel = Value, Value = 20, Source = external });
        systems.Update(ref context);
        Assert.Equal(12.5f, world.Read<Display>(target).Value);
        systems.Update(ref context);

        Assert.Equal(5f, world.Read<Display>(target).Value);
    }

    [Fact]
    public void PhaseSystemsReadCurrentWeightsAndComposedProperties()
    {
        using var world = new World();
        Entity target = world.CreateEntity(new Display());
        world.Add(target, new AnimationLayers { Count = 1 });
        PropertyAnimation.SetBase(world, target, Value, 0f);
        PropertyAnimation.To(world, target, Value, 10f, Spec.Tween(1));
        PropertyAnimation.To(world, target, Weight, 1f, Spec.Tween(2));
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        AnimationSystems.AddDefaults(systems);
        var weights = new List<(float Weight, float Value)>();
        var properties = new List<(float Weight, float Value)>();
        AnimationSystems.Add(systems, new ReadValues(target, weights), AnimationPhase.LayerComposition);
        AnimationSystems.Add(systems, new ReadValues(target, properties), AnimationPhase.Composition);
        Type[] description = AnimationSystems.Describe(systems);
        var context = new ImmediateSystemContext(world, 0, 0, 0.5);

        systems.Update(ref context);
        systems.Update(ref context);
        systems.Update(ref context);

        Assert.Equal(new[] { (0.25f, 0f), (0.5f, 1.25f), (0.75f, 5f) }, weights);
        Assert.Equal(new[] { (0.25f, 1.25f), (0.5f, 5f), (0.75f, 7.5f) }, properties);
        Assert.Equal(description, AnimationSystems.Describe(systems));
    }

    [Fact]
    public void UserPhaseSystemsKeepTheirLifecycleAndObservationWindow()
    {
        using var world = new World();
        var updates = new List<(AnimationPhase Phase, uint Last, uint Current)>();
        var created = new List<AnimationPhase>();
        var destroyed = new List<AnimationPhase>();
        AnimationPhase[] phases = [AnimationPhase.Timeline, AnimationPhase.Graph, AnimationPhase.LayerWeights,
            AnimationPhase.LayerComposition, AnimationPhase.Properties, AnimationPhase.CompositionInputs,
            AnimationPhase.Composition, AnimationPhase.Completion];
        using (var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world)))
        {
            AnimationSystems.AddDefaults(systems);
            foreach (AnimationPhase phase in phases)
                AnimationSystems.Add(systems, new ObservePhase(phase, updates, created, destroyed), phase);

            systems.Update();
            systems.Update();
        }

        Assert.Equal(phases, created);
        Assert.Equal(phases, destroyed);
        Assert.Equal(phases.Concat(phases), updates.Select(update => update.Phase));
        for (int index = 0; index < phases.Length; index++)
        {
            Assert.Equal(0u, updates[index].Last);
            Assert.Equal(updates[index].Current, updates[index + phases.Length].Last);
        }
    }

    private struct Display : IComponent
    {
        public float Value;
    }

    private readonly record struct ReadValues(Entity Target, List<(float Weight, float Value)> Values) : ISystem<ImmediateSystemContext>
    {
        public void OnUpdate(ref ImmediateSystemContext context) =>
            Values.Add((context.World.Read<AnimationLayers>(Target).Items[0].Weight, context.World.Read<Display>(Target).Value));
    }

    private readonly record struct ObservePhase(AnimationPhase Phase,
        List<(AnimationPhase Phase, uint Last, uint Current)> Updates,
        List<AnimationPhase> Created, List<AnimationPhase> Destroyed) : ISystem<ImmediateSystemContext>
    {
        public void OnCreate(ref ImmediateSystemContext context) => Created.Add(Phase);
        public void OnUpdate(ref ImmediateSystemContext context) => Updates.Add((Phase, context.LastSystemVersion, context.CurrentSystemVersion));
        public void OnDestroy(ref ImmediateSystemContext context) => Destroyed.Add(Phase);
    }
}
