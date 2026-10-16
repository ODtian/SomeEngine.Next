using SomeEngine.Animation.Consumers;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Animation.Tests;

public class TypedGraphTests
{
    private static readonly Channel<float> Value = Channel<float>.Of<GraphValue>(static (ref GraphValue value) => ref value.Value);

    [Fact]
    public void SequenceCapturesThePreviousTypedLeafEndpointAcrossAlgorithms()
    {
        using var world = new World();
        Entity target = world.CreateEntity(new GraphValue());
        var next = Spec.Tween<float, FloatOps, SquareEasing>(0.5f);
        using var builder = new AnimationBuilder(world);
        using (builder.Sequence())
        {
            builder.To(target, Value, 10, Spec.Tween<float, FloatOps, Linear>(0.5f));
            builder.To(target, Value, 20, next);
        }
        builder.Play();
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        AnimationSystems.AddDefaults(systems);
        AnimationSystems.Add(systems, next);
        var frame = new ImmediateSystemContext(world, 0, 0, 0.75);

        systems.Update(ref frame);

        Assert.Equal(12.5f, world.Read<GraphValue>(target).Value);
    }

    [Fact]
    public void ConsumerSettlementStartsTheFollowingFixedInterval()
    {
        using var world = new World();
        Entity target = world.CreateEntity(new GraphValue());
        var settled = Spec.Create<float, FloatOps, StepsParameters, StepsState, StepsMotion>(new(2, "settle"));
        using var builder = new AnimationBuilder(world);
        using (builder.Sequence())
        {
            builder.To(target, Value, 4, settled);
            builder.To(target, Value, 8, Spec.Tween<float, FloatOps, Linear>(1));
        }
        builder.Play();
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        AnimationSystems.AddDefaults(systems);
        AnimationSystems.Add(systems, settled);
        var step = new ImmediateSystemContext(world, 0, 0, 0.1);
        systems.Update(ref step);
        systems.Update(ref step);
        var following = new ImmediateSystemContext(world, 0, 0, 0.5);

        systems.Update(ref following);

        Assert.Equal(6, world.Read<GraphValue>(target).Value);
    }

    [Fact]
    public void SharedDefinitionKeepsTheTwoInstanceClocksIndependent()
    {
        using var world = new World();
        Entity first = world.CreateEntity(new GraphValue());
        Entity second = world.CreateEntity(new GraphValue());
        var spec = Spec.Create<float, FloatOps, HistoryParameters, HistoryState, HistoryMotion>(new(1, 1, "shared"));
        using var builder = new AnimationBuilder(world);
        builder.To(first, Value, 10, spec);
        using CompiledGraph graph = builder.Compile();
        Entity animation = graph.Play(world, first);
        graph.Play(world, second);
        Timeline timeline = world.Read<Timeline>(animation);
        timeline.Speed = 2;
        world.Replace(animation, timeline);
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        AnimationSystems.Add(systems, spec);
        var frame = new ImmediateSystemContext(world, 0, 0, 0.25);

        systems.Update(ref frame);

        Assert.Equal((5, 2.5f), (world.Read<GraphValue>(first).Value, world.Read<GraphValue>(second).Value));
    }

    [Fact]
    public void FormulaParametersChangeOnlyTheSelectedSharedInstance()
    {
        using var world = new World();
        Entity first = world.CreateEntity(new GraphValue());
        Entity second = world.CreateEntity(new GraphValue());
        var spec = Spec.Create<float, FloatOps, HistoryParameters, HistoryState, HistoryMotion>(new(1, 1, "shared"));
        using var builder = new AnimationBuilder(world);
        builder.To(first, Value, 10, spec);
        using CompiledGraph graph = builder.Compile();
        Entity animation = graph.Play(world, first);
        graph.Play(world, second);
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        AnimationSystems.Add(systems, spec);
        var frame = new ImmediateSystemContext(world, 0, 0, 0.25);
        systems.Update(ref frame);
        var changed = Spec.Create<float, FloatOps, HistoryParameters, HistoryState, HistoryMotion>(new(1, 2, "instance"));
        AnimationGraph.UpdateParameters(world, animation, Value, in changed);

        systems.Update(ref frame);

        Assert.Equal((10, 5), (world.Read<GraphValue>(first).Value, world.Read<GraphValue>(second).Value));
    }

    [Fact]
    public void RetargetingOneSharedInstanceKeepsTheCompiledDeadline()
    {
        using var world = new World();
        Entity first = world.CreateEntity(new GraphValue());
        Entity second = world.CreateEntity(new GraphValue());
        var spec = Spec.Create<float, FloatOps, HistoryParameters, HistoryState, HistoryMotion>(new(1, 1, "retarget"));
        using var builder = new AnimationBuilder(world);
        builder.To(first, Value, 10, spec);
        using CompiledGraph graph = builder.Compile();
        Entity animation = graph.Play(world, first);
        graph.Play(world, second);
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        AnimationSystems.Add(systems, spec);
        var half = new ImmediateSystemContext(world, 0, 0, 0.5);
        systems.Update(ref half);
        PropertyAnimation.Retarget(world, animation, Value, 20);

        systems.Update(ref half);

        Assert.Equal((20, 10), (world.Read<GraphValue>(first).Value, world.Read<GraphValue>(second).Value));
    }

    [Fact]
    public void PublicLeafIsReleasedOnceWhenItsEntityIsStoppedAndReused()
    {
        using var world = new World();
        Entity target = world.CreateEntity(new MarkerValue());
        using var builder = new AnimationBuilder(world);
        builder.Leaf<TimedMarkerParameters, TimedMarkerLeaf>(target, new(1, 7));
        using CompiledGraph graph = builder.Compile();
        Entity animation = graph.Play(world, target);

        PropertyAnimation.Stop(world, animation);
        graph.Play(world, target);

        Assert.Equal(1, world.Read<MarkerReleases>(animation).Count);
    }

    [Fact]
    public void ConsumerConditionTransitionsOnlyTheMatchingInstance()
    {
        using var world = new World();
        Entity first = world.CreateEntity(new GraphValue());
        Entity second = world.CreateEntity(new GraphValue());
        var inputs = new GraphParameters();
        using var builder = new AnimationBuilder(world);
        using (var machine = builder.StateMachine(ref inputs))
        {
            using (machine.State(0)) builder.To(first, Value, 0, Spec.Tween<float, FloatOps, Linear>(0));
            using (machine.State(1)) builder.To(first, Value, 10, Spec.Tween<float, FloatOps, Linear>(0));
            machine.Transition(0, 1, GraphCondition.Create<AfterSamplesParameters, AfterSamplesState, AfterSamplesCondition>(new(0, 1, 2, "ready")));
        }
        using CompiledGraph graph = builder.Compile();
        Entity animation = graph.Play(world, first);
        graph.Play(world, second);
        inputs.Values[0] = 1;
        world.Replace(animation, inputs);
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        AnimationSystems.AddDefaults(systems);
        var frame = new ImmediateSystemContext(world, 0, 0, 0);
        systems.Update(ref frame);

        systems.Update(ref frame);

        Assert.Equal((10, 0), (world.Read<GraphValue>(first).Value, world.Read<GraphValue>(second).Value));
    }

    private struct GraphValue : IComponent { public float Value; }
}
