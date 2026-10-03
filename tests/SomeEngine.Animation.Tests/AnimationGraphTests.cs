using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Animation.Tests;

public class AnimationGraphTests
{
    private static readonly Channel<float> Value = Channel<float>.Of<Display>(static (ref Display value) => ref value.Value);
    private static readonly Channel<float> Other = Channel<float>.Of<Display>(static (ref Display value) => ref value.Other);

    [Fact]
    public void NestedTimingUsesOneAnimationAndMatchesWorkedIntervals()
    {
        using var world = new World();
        Entity first = world.CreateEntity(new Display());
        Entity second = world.CreateEntity(new Display());
        using var graph = new AnimationBuilder(world);
        using (graph.Sequence())
        {
            graph.To(first, Value, 10f, Spec.Tween(1));
            graph.Delay(0.5f);
            using (graph.Parallel())
            {
                graph.To(first, Other, 20f, Spec.Tween(1));
                using (graph.Stagger(0.5f))
                {
                    graph.To(second, Value, 30f, Spec.Tween(1));
                    graph.To(second, Other, 40f, Spec.Tween(1));
                }
            }
        }
        Entity animation = graph.Play();
        using var systems = Systems(world);

        Assert.Equal(3, world.EntityCount);
        Tick(systems, world, 0.5);
        Assert.Equal(5, world.Read<Display>(first).Value);
        Assert.Equal(0, world.Read<Display>(second).Value);
        Tick(systems, world, 1.25);
        Assert.Equal(10, world.Read<Display>(first).Value);
        Assert.Equal(5, world.Read<Display>(first).Other);
        Assert.Equal(7.5f, world.Read<Display>(second).Value);
        Assert.Equal(0, world.Read<Display>(second).Other);
        Tick(systems, world, 0.5);
        Assert.Equal(15, world.Read<Display>(first).Other);
        Assert.Equal(22.5f, world.Read<Display>(second).Value);
        Assert.Equal(10, world.Read<Display>(second).Other);
        Tick(systems, world, 0.75);
        Assert.Equal(20, world.Read<Display>(first).Other);
        Assert.Equal(30, world.Read<Display>(second).Value);
        Assert.Equal(40, world.Read<Display>(second).Other);
        Assert.False(world.IsEnabled<Timeline>(animation));
    }

    [Fact]
    public void SpringSequenceEntersTheNextIntervalAfterSettlementOnItsTarget()
    {
        using var world = new World();
        Entity target = world.CreateEntity(new Display());
        using var graph = new AnimationBuilder(world);
        using (graph.Sequence())
        {
            graph.To(target, Value, 10f, Spec.Tween(0.25f));
            graph.To(target, Value, 20f, Spec.Spring(tolerance: 0.0001f));
            graph.To(target, Value, 30f, Spec.Tween(0.25f));
        }
        Entity animation = graph.Play();
        using var systems = Systems(world);

        Assert.Equal(target, animation);
        Assert.Equal(1, world.EntityCount);
        Tick(systems, world, 0.25);
        Assert.Equal(10, world.Read<Display>(target).Value);
        for (int frame = 0; frame < 600 && world.Read<Timeline>(animation).Stage == 0; frame++)
            Tick(systems, world, 1d / 60);
        Assert.Equal(20, world.Read<Display>(target).Value);
        Tick(systems, world, 0.125);
        Assert.Equal(25, world.Read<Display>(target).Value);
        Tick(systems, world, 0.125);
        Assert.Equal(30, world.Read<Display>(target).Value);
        Assert.False(world.IsEnabled<Timeline>(target));
        Assert.Equal(1, world.EntityCount);
    }

    [Fact]
    public void RepeatedDisplacementsStartAfterTheDriverAdvancesTheBoundary()
    {
        using var world = new World();
        Entity target = world.CreateEntity(new Display { Value = 10 });
        using var graph = new AnimationBuilder(world);
        using (graph.Sequence())
        {
            graph.Boundary(StageEnd.External);
            using (graph.Repeat(2))
                graph.By(target, Value, 4f, Spec.Tween(0.5f));
        }
        Entity animation = graph.Play();
        using var systems = Systems(world);

        Tick(systems, world, 1);
        Assert.Equal(10, world.Read<Display>(target).Value);
        Animation.Advance(world, animation);
        Tick(systems, world, 0.25);
        Assert.Equal(12, world.Read<Display>(target).Value);
        Tick(systems, world, 0.5);
        Assert.Equal(12, world.Read<Display>(target).Value);
        Tick(systems, world, 0.25);
        Assert.Equal(10, world.Read<Display>(target).Value);
        Assert.False(world.IsEnabled<Timeline>(animation));
    }

    [Fact]
    public void BranchHandlePausesIndependentlyAndFusedEntitiesAreReused()
    {
        using var world = new World();
        Entity first = world.CreateEntity(new Display());
        Entity second = world.CreateEntity(new Display());
        using var systems = Systems(world);
        using var graph = new AnimationBuilder(world);
        graph.To(first, Value, 10f, Spec.Tween(1));
        graph.To(second, Value, 20f, Spec.Tween(1));
        int branchId;
        using (var branch = graph.Branch())
        {
            branchId = branch.Id;
            graph.To(second, Other, 30f, Spec.Tween(1));
        }
        Entity animation = graph.Play();
        Entity independent = AnimationGraph.GetBranch(world, animation, branchId);
        PropertyAnimation.Pause(world, independent);
        Tick(systems, world, 0.5);
        Assert.Equal(5, world.Read<Display>(first).Value);
        Assert.Equal(10, world.Read<Display>(second).Value);
        Assert.Equal(0, world.Read<Display>(second).Other);
        PropertyAnimation.Resume(world, independent);
        Tick(systems, world, 0.5);
        Assert.Equal(15, world.Read<Display>(second).Other);
        Tick(systems, world, 0.5);
        Assert.Equal(30, world.Read<Display>(second).Other);

        int count = world.EntityCount;
        graph.To(first, Value, 0f, Spec.Tween(1));
        graph.To(second, Value, 0f, Spec.Tween(1));
        Entity replay = graph.Play();
        Assert.Equal(animation, replay);
        Assert.Equal(count, world.EntityCount);
    }

    [Fact]
    public void ButtonStatesReadDriverParametersAndCrossfadeOnOneEntity()
    {
        using var world = new World();
        Entity button = world.CreateEntity(new Display());
        var parameters = new GraphParameters();
        using var graph = new AnimationBuilder(world);
        using (var machine = graph.StateMachine(ref parameters))
        {
            using (machine.State(0, initial: true))
                graph.To(button, Value, 0f, Spec.Tween(0));
            using (machine.State(1))
                graph.To(button, Value, 10f, Spec.Tween(0));
            using (machine.State(2))
                graph.To(button, Value, 20f, Spec.Tween(0));
            machine.Transition(0, 1, GraphCondition.Equal(0, 1), crossfade: 0.2f);
            machine.Transition(1, 2, GraphCondition.Equal(0, 2), crossfade: 0.2f);
            machine.Transition(2, 0, GraphCondition.Equal(0, 0), crossfade: 0.2f);
        }
        Entity animation = graph.Play();
        using var systems = Systems(world);
        Tick(systems, world, 0);
        Assert.Equal(button, animation);
        Assert.Equal(1, world.EntityCount);
        Assert.Equal(0, world.Read<Display>(button).Value);
        parameters.Values[0] = 1;
        world.Replace(animation, parameters);
        Tick(systems, world, 0.1);
        Assert.Equal(5, world.Read<Display>(button).Value, 5);
        Tick(systems, world, 0.1);
        Assert.Equal(10, world.Read<Display>(button).Value);
        parameters.Values[0] = 2;
        world.Replace(animation, parameters);
        Tick(systems, world, 0.1);
        Assert.Equal(15, world.Read<Display>(button).Value, 5);
        Tick(systems, world, 0.1);
        Assert.Equal(20, world.Read<Display>(button).Value);
        parameters.Values[0] = 0;
        world.Replace(animation, parameters);
        Tick(systems, world, 0.1);
        Assert.Equal(10, world.Read<Display>(button).Value, 5);
        Tick(systems, world, 0.1);
        Assert.Equal(0, world.Read<Display>(button).Value);
        Assert.True(world.IsEnabled<Timeline>(animation));
    }

    [Fact]
    public void BlendCoordinateInterpolatesSamplesAndRespectsPause()
    {
        using var world = new World();
        Entity target = world.CreateEntity(new Display());
        var parameters = new GraphParameters();
        parameters.Values[0] = 0.25f;
        using var graph = new AnimationBuilder(world);
        using (var blend = graph.Blend1D(0, ref parameters))
        {
            using (blend.Point(0))
                graph.To(target, Value, 0f, Spec.Tween(0));
            using (blend.Point(1))
                graph.To(target, Value, 20f, Spec.Tween(0));
        }
        Entity animation = graph.Play();
        using var systems = Systems(world);
        Tick(systems, world, 0);
        Assert.Equal(5, world.Read<Display>(target).Value);
        parameters.Values[0] = 0.75f;
        world.Replace(animation, parameters);
        Tick(systems, world, 0);
        Assert.Equal(15, world.Read<Display>(target).Value);
        parameters.Values[0] = 2;
        world.Replace(animation, parameters);
        Tick(systems, world, 0);
        Assert.Equal(20, world.Read<Display>(target).Value);
        PropertyAnimation.Pause(world, animation);
        parameters.Values[0] = -1;
        world.Replace(animation, parameters);
        Tick(systems, world, 1);
        Assert.Equal(20, world.Read<Display>(target).Value);
        PropertyAnimation.Resume(world, animation);
        Tick(systems, world, 0);
        Assert.Equal(0, world.Read<Display>(target).Value);
        Assert.Equal(1, world.EntityCount);
    }

    [Fact]
    public void LayerScopeCombinesGraphOutputsWithTheCallersBaseValue()
    {
        using var world = new World();
        Entity target = world.CreateEntity(new Display { Value = 10 });
        PropertyAnimation.SetBase(world, target, Value, 10f);
        using var graph = new AnimationBuilder(world);
        using (graph.Layer(0, new AnimationLayer { Mode = LayerMode.Override, Weight = 1 }))
            graph.To(target, Value, 20f, Spec.Tween(1));
        using (graph.Layer(1, new AnimationLayer { Mode = LayerMode.Additive, Weight = 0.5f }))
            graph.To(target, Value, 30f, Spec.Tween(1));
        Entity animation = graph.Play();
        using var systems = Systems(world);

        Tick(systems, world, 0.5);
        Assert.Equal(20, world.Read<Display>(target).Value);
        Tick(systems, world, 0.5);
        Assert.Equal(30, world.Read<Display>(target).Value);
        Assert.Equal(target, animation);
        Assert.Equal(1, world.EntityCount);
    }

    [Fact]
    public void CompiledStatesStartIndependentInstancesAndSurviveTheCompileHandle()
    {
        using var world = new World();
        Entity first = world.CreateEntity(new Display());
        Entity second = world.CreateEntity(new Display());
        var parameters = new GraphParameters();
        using var systems = Systems(world);
        using var builder = new AnimationBuilder(world);
        using (var machine = builder.StateMachine(ref parameters))
        {
            using (machine.State(0))
                builder.To(first, Value, 0f, Spec.Tween(0));
            using (machine.State(1))
                builder.To(first, Value, 10f, Spec.Tween(0));
            machine.Transition(0, 1, GraphCondition.Equal(0, 1), 0.2f);
        }
        CompiledGraph compiled = builder.Compile();
        Entity a = compiled.Play(world, first);
        Entity b = compiled.Play(world, second);
        compiled.Dispose();
        Tick(systems, world, 0);
        parameters.Values[0] = 1;
        world.Replace(a, parameters);
        Tick(systems, world, 0.1);
        Assert.Equal(5, world.Read<Display>(first).Value, 5);
        Assert.Equal(0, world.Read<Display>(second).Value);
        world.Replace(b, parameters);
        PropertyAnimation.Pause(world, a);
        Tick(systems, world, 0.1);
        Assert.Equal(5, world.Read<Display>(first).Value, 5);
        Assert.Equal(5, world.Read<Display>(second).Value, 5);
        Assert.Equal(2, world.EntityCount);
    }

    [Fact]
    public void WarmConstructionCompilationAndPlaybackAllocateZeroManagedBytes()
    {
        using var world = new World();
        Entity first = world.CreateEntity(new Display());
        Entity second = world.CreateEntity(new Display());
        using var systems = Systems(world);
        for (int iteration = 0; iteration < 512; iteration++)
            BuildAndPlay(world, systems, first, second);
        int count = world.EntityCount;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int iteration = 0; iteration < 128; iteration++)
            BuildAndPlay(world, systems, first, second);
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, bytes);
        Assert.Equal(count, world.EntityCount);
        Assert.Equal(10, world.Read<Display>(first).Value);
        Assert.Equal(20, world.Read<Display>(second).Value);
    }

    [Fact]
    public void StateSequencesContinueAfterTheirSpringSettles()
    {
        using var world = new World();
        Entity target = world.CreateEntity(new Display());
        var parameters = new GraphParameters();
        using var graph = new AnimationBuilder(world);
        using (var machine = graph.StateMachine(ref parameters))
        using (machine.State(0))
        using (graph.Sequence())
        {
            graph.To(target, Value, 10f, Spec.Spring(damping: 30, tolerance: 0.01f));
            graph.To(target, Value, 20f, Spec.Tween(0.25f));
        }
        Entity animation = graph.Play();
        using var systems = Systems(world);
        for (int frame = 0; frame < 600 && world.Read<Display>(target).Value != 10; frame++)
            Tick(systems, world, 1d / 60);
        Assert.Equal(10, world.Read<Display>(target).Value);
        Tick(systems, world, 0.125);
        Assert.Equal(15, world.Read<Display>(target).Value);
        Tick(systems, world, 0.125);
        Assert.Equal(20, world.Read<Display>(target).Value);
        Assert.True(world.IsEnabled<Timeline>(animation));
    }

    [Fact]
    public void CapacitySplitsKeepTheGraphsTimeAndPauseTogether()
    {
        using var world = new World();
        var targets = new Entity[65];
        using var graph = new AnimationBuilder(world);
        for (int i = 0; i < targets.Length; i++)
        {
            targets[i] = world.CreateEntity(new Display());
            graph.To(targets[i], Value, 10f, Spec.Tween(1));
        }
        Entity animation = graph.Play();
        using var systems = Systems(world);
        Assert.Equal(66, world.EntityCount);
        Tick(systems, world, 0.25);
        foreach (Entity target in targets)
            Assert.Equal(2.5f, world.Read<Display>(target).Value);
        PropertyAnimation.Pause(world, animation);
        Tick(systems, world, 0.5);
        foreach (Entity target in targets)
            Assert.Equal(2.5f, world.Read<Display>(target).Value);
        PropertyAnimation.Resume(world, animation);
        Tick(systems, world, 0.75);
        foreach (Entity target in targets)
            Assert.Equal(10, world.Read<Display>(target).Value);
        Assert.False(world.IsEnabled<Timeline>(animation));
    }

    [Fact]
    public void IndependentMultiTargetGraphsCombineTheirAdditiveFields()
    {
        using var world = new World();
        Entity first = world.CreateEntity(new Display { Value = 10 });
        Entity second = world.CreateEntity(new Display());
        var layers = new AnimationLayers { Count = 1 };
        layers.Items[0] = new AnimationLayer { Mode = LayerMode.Additive, Weight = 1 };
        world.Add(first, layers);
        PropertyAnimation.SetBase(world, first, Value, 10f);
        using var systems = Systems(world);
        using var graph = new AnimationBuilder(world);
        graph.To(first, Value, 20f, Spec.Tween(1));
        graph.To(second, Value, 10f, Spec.Tween(1));
        graph.Play();
        graph.To(first, Value, 30f, Spec.Tween(1));
        graph.To(second, Other, 20f, Spec.Tween(1));
        graph.Play();

        Tick(systems, world, 0.5);
        Assert.Equal(25, world.Read<Display>(first).Value);
        Tick(systems, world, 0.5);
        Assert.Equal(40, world.Read<Display>(first).Value);
    }

    [Fact]
    public void ParallelSequencesContinueAtTheirOwnOpenBoundaries()
    {
        using var world = new World();
        Entity target = world.CreateEntity(new Display());
        using var graph = new AnimationBuilder(world);
        using (graph.Parallel())
        {
            using (graph.Sequence())
            {
                graph.To(target, Value, 10f, Spec.Spring(damping: 30, tolerance: 0.01f));
                graph.To(target, Value, 20f, Spec.Tween(0.25f));
            }
            using (graph.Sequence())
            {
                graph.Delay(10);
                graph.To(target, Other, 30f, Spec.Tween(1));
            }
        }
        Entity animation = graph.Play();
        using var systems = Systems(world);
        for (int frame = 0; frame < 600 && world.Read<Display>(target).Value != 10; frame++)
            Tick(systems, world, 1d / 60);
        Tick(systems, world, 0.25);
        Assert.Equal(20, world.Read<Display>(target).Value);
        Assert.Equal(0, world.Read<Display>(target).Other);
        Assert.True(world.IsEnabled<Timeline>(animation));
        Tick(systems, world, 11);
        Assert.Equal(30, world.Read<Display>(target).Other);
        Assert.False(world.IsEnabled<Timeline>(animation));
        Assert.Equal(1, world.EntityCount);
    }

    private static void BuildAndPlay(World world, SystemGroup<ImmediateSystemContext> systems, Entity first, Entity second)
    {
        using var graph = new AnimationBuilder(world);
        using (graph.Sequence())
        {
            graph.To(first, Value, 0f, Spec.Tween(0.1f));
            graph.Delay(0.1f);
            using (graph.Parallel())
            {
                graph.To(first, Value, 10f, Spec.Tween(0.1f));
                graph.To(second, Value, 20f, Spec.Tween(0.1f));
            }
        }
        graph.Play();
        Tick(systems, world, 1);
    }

    [Fact]
    public void WarmStateGraphBuildsAndFramesAllocateZeroManagedBytes()
    {
        using var world = new World();
        Entity target = world.CreateEntity(new Display());
        using var systems = Systems(world);
        for (int i = 0; i < 512; i++)
            BuildStateAndPlay(world, systems, target);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 128; i++)
            BuildStateAndPlay(world, systems, target);
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, bytes);
        Assert.Equal(10, world.Read<Display>(target).Value);
        Assert.Equal(1, world.EntityCount);
    }

    private static void BuildStateAndPlay(World world, SystemGroup<ImmediateSystemContext> systems, Entity target)
    {
        using var graph = new AnimationBuilder(world);
        var parameters = new GraphParameters();
        using (var machine = graph.StateMachine(ref parameters))
        {
            using (machine.State(0))
                graph.To(target, Value, 0f, Spec.Tween(0));
            using (machine.State(1))
                graph.To(target, Value, 10f, Spec.Tween(0));
            machine.Transition(0, 1, GraphCondition.Equal(0, 1), 0.25f);
        }
        Entity animation = graph.Play();
        Tick(systems, world, 0);
        parameters.Values[0] = 1;
        world.Replace(animation, parameters);
        Tick(systems, world, 0.25);
        PropertyAnimation.Stop(world, animation);
    }

    [Fact]
    public void UserNodesAndValueOperationsUseTheSameGraphApi()
    {
        using var world = new World();
        Entity target = world.CreateEntity(new Reading());
        Channel<Meter> channel = Channel<Meter>.Of<Reading>(static (ref Reading value) => ref value.Value);
        using var graph = new AnimationBuilder(world);
        AddRiseAndFall(graph, target, channel);
        Entity animation = graph.Play();
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        systems.Add(new TimelineSystem());
        systems.Add(new GraphSystem());
        systems.Add(new TrackSystem<Meter, MeterOps>());
        systems.Add(new ComposeSystem<Meter, MeterOps>());
        systems.Add(new GraphEndSystem());
        systems.Add(new TimelineEndSystem());
        Tick(systems, world, 0.5);
        Assert.Equal(new Meter(5), world.Read<Reading>(target).Value);
        Tick(systems, world, 1);
        Assert.Equal(new Meter(5), world.Read<Reading>(target).Value);
        Tick(systems, world, 0.5);
        Assert.Equal(new Meter(0), world.Read<Reading>(target).Value);
        Assert.Equal(target, animation);
    }

    private static void AddRiseAndFall(AnimationBuilder graph, Entity target, Channel<Meter> channel)
    {
        using (graph.Sequence())
        {
            graph.To(target, channel, new Meter(10), Spec.Tween(1));
            graph.To(target, channel, new Meter(0), Spec.Tween(1));
        }
    }

    [Fact]
    public void ActiveInlineGraphsKeepTheirOwnTimeWhenANewGraphStarts()
    {
        using var world = new World();
        Entity target = world.CreateEntity(new Display());
        using var systems = Systems(world);
        using var graph = new AnimationBuilder(world);
        graph.To(target, Value, 10f, Spec.Tween(1));
        Entity first = graph.Play();
        graph.To(target, Other, 20f, Spec.Tween(1));
        Entity second = graph.Play();
        Assert.Equal(target, first);
        Assert.NotEqual(first, second);
        PropertyAnimation.Pause(world, first);
        Tick(systems, world, 0.5);
        Assert.Equal(0, world.Read<Display>(target).Value);
        Assert.Equal(10, world.Read<Display>(target).Other);
    }

    [Fact]
    public void DurationOnlyGraphsKeepTheirHandleEnabledForTheWait()
    {
        using var world = new World();
        using var graph = new AnimationBuilder(world);
        graph.Delay(0.5f);
        Entity animation = graph.Play();
        using var systems = Systems(world);
        Tick(systems, world, 0.25);
        Assert.True(world.IsEnabled<Timeline>(animation));
        Tick(systems, world, 0.25);
        Assert.False(world.IsEnabled<Timeline>(animation));
    }

    public readonly record struct Meter(float Value);
    [Fact]
    public void DestroyedHandlesFinishCleanupWhileSharedInstancesContinue()
    {
        using var world = new World();
        Entity first = world.CreateEntity(new Display());
        Entity second = world.CreateEntity(new Display());
        using var systems = Systems(world);
        using var graph = new AnimationBuilder(world);
        var parameters = new GraphParameters();
        using (var machine = graph.StateMachine(ref parameters))
        {
            using (machine.State(0)) graph.To(first, Value, 0f, Spec.Tween(0));
            using (machine.State(1)) graph.To(first, Value, 10f, Spec.Tween(0));
            machine.Transition(0, 1, GraphCondition.Equal(0, 1));
        }
        using CompiledGraph compiled = graph.Compile();
        Entity a = compiled.Play(world, first);
        Entity b = compiled.Play(world, second);
        world.DestroyEntity(a);
        parameters.Values[0] = 1;
        world.Replace(b, parameters);
        Tick(systems, world, 0);
        world.Flush();
        Assert.False(world.IsAlive(a));
        Assert.Equal(10, world.Read<Display>(second).Value);
    }

    public struct Reading : IComponent { public Meter Value; }
    public readonly struct MeterOps : IValueOps<Meter>
    {
        public static Meter Lerp(in Meter a, in Meter b, float t) => new(a.Value + (b.Value - a.Value) * t);
        public static Meter Add(in Meter a, in Meter b) => new(a.Value + b.Value);
        public static Meter Subtract(in Meter a, in Meter b) => new(a.Value - b.Value);
        public static Meter Scale(in Meter a, float scale) => new(a.Value * scale);
        public static float Distance(in Meter a, in Meter b) => MathF.Abs(a.Value - b.Value);
    }

#if DEBUG
    [Fact]
    public void ConstructionErrorsAreReportedThroughDevelopmentChecks()
    {
        using var world = new World();
        Assert.Throws<InvalidOperationException>(() =>
        {
            using var graph = new AnimationBuilder(world);
            graph.Repeat(0);
        });
        Assert.Throws<InvalidOperationException>(() =>
        {
            using var graph = new AnimationBuilder(world);
            using var sequence = graph.Sequence();
            graph.Compile();
        });
        Assert.Throws<InvalidOperationException>(() =>
        {
            using var graph = new AnimationBuilder(world);
            var parameters = new GraphParameters();
            using (var machine = graph.StateMachine(ref parameters))
            {
                using (machine.State(0)) graph.Delay(1);
                machine.Transition(0, 9, GraphCondition.Equal(0, 1));
            }
            graph.Compile();
        });
    }
#endif

    private static SystemGroup<ImmediateSystemContext> Systems(World world)
    {
        var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        AnimationSystems.AddDefaults(systems);
        return systems;
    }

    private static void Tick(SystemGroup<ImmediateSystemContext> systems, World world, double seconds)
    {
        var frame = new ImmediateSystemContext(world, 0, 0, seconds);
        systems.Update(ref frame);
    }

    public struct Display : IComponent
    {
        public float Value;
        public float Other;
    }
}
