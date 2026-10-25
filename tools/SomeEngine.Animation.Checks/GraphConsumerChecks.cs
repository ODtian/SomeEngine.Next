using SomeEngine.Animation;
using SomeEngine.Animation.Consumers;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Animation.Checks;

internal static class GraphConsumerChecks
{
    private static readonly Channel<float> Value = Channel<float>.Of<GraphValue>(static (ref GraphValue value) => ref value.Value);
    private static readonly Channel<float> Other = Channel<float>.Of<GraphValue>(static (ref GraphValue value) => ref value.Other);

    internal static void Check()
    {
        CheckSequence();
        CheckPhaseCreation();
        CheckSharedInstances();
        CheckInstanceParameters();
        CheckRetarget();
        CheckCustomCondition();
        CheckPublicLeaf();
        CheckOutputRemoval();
        Console.WriteLine("GRAPH_CONSUMERS_PASSED typed-sequence phase-create shared-definition instance-state custom-condition public-leaf");
    }

    private static void CheckSequence()
    {
        using var world = new World();
        Entity target = world.CreateEntity(new GraphValue());
        var first = Spec.Tween<float, FloatOps, Linear>(0.5f);
        var second = Spec.Tween<float, FloatOps, SquareEasing>(0.5f);
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        AnimationSystems.AddDefaults(systems);
        AnimationSystems.Add(systems, second);
        Tick(systems, world, 0.25);
        using var builder = new AnimationBuilder(world);
        using (builder.Sequence())
        {
            builder.To(target, Value, 10, first);
            builder.To(target, Value, 20, second);
        }
        builder.Play();
        Tick(systems, world, 0.75);
        Require(world.Read<GraphValue>(target).Value == 12.5f, "Typed sequential leaves sample their compiled intervals.");
    }

    private static void CheckPhaseCreation()
    {
        using var world = new World();
        Entity target = world.CreateEntity(new GraphValue());
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        var creator = new PhaseGraph(target);
        AnimationSystems.AddDefaults(systems);
        AnimationSystems.Add(systems, creator, AnimationPhase.Graph);
        Type[] described = AnimationSystems.Describe(systems);
        Tick(systems, world, .25);
        creator.Create = true;
        Tick(systems, world, .25);
        Require(world.Read<GraphValue>(target).Value == 10,
            "A property graph created in the Graph phase must reach its instant target in the same update.");
        Require(described.SequenceEqual(AnimationSystems.Describe(systems)),
            "Concrete phase systems retain their described order before and after creation.");

        using var continuous = new AnimationBuilder(world);
        continuous.To(target, Value, 1034, Spec.Tween<float, FloatOps, Linear>(1024));
        continuous.Play();
        for (int frame = 0; frame < 64; frame++) Tick(systems, world, .25);
        long before = GC.GetTotalAllocatedBytes(true);
        for (int frame = 0; frame < 64; frame++) Tick(systems, world, .25);
        long bytes = GC.GetTotalAllocatedBytes(true) - before;
        Require(bytes == 0 && world.Read<GraphValue>(target).Value == 42,
            "The active property pipeline must advance its declared graph with zero steady process allocation.");
        Console.WriteLine($"PROPERTY_PIPELINE_ALLOCATIONS samples=64 bytes={bytes}");
    }

    private sealed class PhaseGraph(Entity target) : ISystem<ImmediateSystemContext>
    {
        internal bool Create;

        public void OnUpdate(ref ImmediateSystemContext context)
        {
            if (!Create) return;
            Create = false;
            using var builder = new AnimationBuilder(context.World);
            builder.To(target, Value, 10, Spec.Tween<float, FloatOps, Linear>(0));
            builder.Play();
        }
    }

    private static void CheckSharedInstances()
    {
        using var world = new World();
        Entity first = world.CreateEntity(new GraphValue());
        Entity second = world.CreateEntity(new GraphValue());
        var spec = Spec.Create<float, FloatOps, HistoryParameters, HistoryState, HistoryMotion>(new(1, 1, "shared"));
        using var builder = new AnimationBuilder(world);
        builder.To(first, Value, 10, spec);
        using CompiledGraph graph = builder.Compile();
        Entity a = graph.Play(world, first);
        graph.Play(world, second);
        Timeline time = world.Read<Timeline>(a);
        time.Speed = 2;
        world.Replace(a, time);
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        AnimationSystems.Add(systems, spec);
        Tick(systems, world, 0.25);
        Require(world.Read<GraphValue>(first).Value == 5 && world.Read<GraphValue>(second).Value == 2.5f,
            "Shared parameters produce independent instance times and state.");
    }

    private static void CheckCustomCondition()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 0, SafetyMode = JobSafetyMode.Checked });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        Entity first = world.CreateEntity(new GraphValue());
        Entity second = world.CreateEntity(new GraphValue());
        var parameters = new GraphParameters();
        using var builder = new AnimationBuilder(world);
        using (var machine = builder.StateMachine(ref parameters))
        {
            using (machine.State(0)) builder.To(first, Value, 0, Spec.Tween<float, FloatOps, Linear>(0));
            using (machine.State(1)) builder.To(first, Value, 10, Spec.Tween<float, FloatOps, Linear>(0));
            machine.Transition(0, 1, GraphCondition.Create<AfterSamplesParameters, AfterSamplesState, AfterSamplesCondition>(new(0, 1, 2, "ready")));
        }
        using CompiledGraph graph = builder.Compile();
        Entity a = graph.Play(world, first);
        graph.Play(world, second);
        parameters.Values[0] = 1;
        world.Replace(a, parameters);
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        AnimationSystems.AddDefaults(systems);
        Tick(systems, world, 0);
        Tick(systems, world, 0);
        Require(world.Read<GraphValue>(first).Value == 10 && world.Read<GraphValue>(second).Value == 0,
            "A consumer condition advances only the instance with matching graph input.");
    }

    private static void CheckRetarget()
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
        Tick(systems, world, 0.5);
        PropertyAnimation.Retarget(world, animation, Value, 20);
        Tick(systems, world, 0.25);
        Require(world.Read<GraphValue>(first).Value == 12.5f && world.Read<GraphValue>(second).Value == 7.5f,
            "Retargeting uses the instance's current output and the remaining compiled interval.");
        Tick(systems, world, 0.25);
        Require(world.Read<GraphValue>(first).Value == 20 && world.Read<GraphValue>(second).Value == 10,
            "Both instances reach their own endpoint at the shared definition's compiled deadline.");
    }

    private static void CheckInstanceParameters()
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
        Tick(systems, world, 0.25);
        var changed = Spec.Create<float, FloatOps, HistoryParameters, HistoryState, HistoryMotion>(new(1, 2, "instance"));
        AnimationGraph.UpdateParameters(world, animation, Value, in changed);

        Tick(systems, world, 0.25);

        Require(world.Read<GraphValue>(first).Value == 10 && world.Read<GraphValue>(second).Value == 5,
            "Formula parameter changes retain one instance's time while the other keeps its shared parameters.");
    }

    private static void CheckPublicLeaf()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 0, SafetyMode = JobSafetyMode.Checked });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        Entity first = world.CreateEntity(new MarkerValue());
        Entity second = world.CreateEntity(new MarkerValue());
        using var builder = new AnimationBuilder(world);
        builder.Leaf<TimedMarkerParameters, TimedMarkerLeaf>(first, new(1, 7));
        using CompiledGraph graph = builder.Compile();
        Entity a = graph.Play(world, first);
        Entity b = graph.Play(world, second);
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        AnimationSystems.Add(systems, new MarkerSystem(), AnimationPhase.Properties);
        Tick(systems, world, 1);
        Require(world.Read<MarkerValue>(first).Value == 7 && world.Read<MarkerValue>(second).Value == 7,
            "Public leaf installation and phase submission reach both replacement targets.");
        var shared = (Definition: (AnimationLeafBatch<TimedMarkerParameters>?)null, Same: false);
        world.ExecuteBufferRead<MarkerState, (AnimationLeafBatch<TimedMarkerParameters>? Definition, bool Same)>(a, ref shared,
            static (states, ref shared) => shared.Definition = states[0].Definition);
        world.ExecuteBufferRead<MarkerState, (AnimationLeafBatch<TimedMarkerParameters>? Definition, bool Same)>(b, ref shared,
            static (states, ref shared) => shared.Same = ReferenceEquals(shared.Definition, states[0].Definition));
        Require(shared.Same, "Leaf instances reference the same compiled definition.");
        PropertyAnimation.Stop(world, a);
        Entity reused = graph.Play(world, first);
        Require(reused == a && world.Read<MarkerReleases>(a).Count == 1,
            "Stopping releases the consumer leaf once and its entity accepts the shared definition again.");
    }

    private static void Tick(SystemGroup<ImmediateSystemContext> systems, World world, double delta)
    {
        var context = new ImmediateSystemContext(world, 0, 0, delta);
        systems.Update(ref context);
    }

    private static void CheckOutputRemoval()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 0, SafetyMode = JobSafetyMode.Checked });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        Entity target = world.CreateEntity(new GraphValue());
        var spec = Spec.Tween<float, FloatOps, Linear>(1);
        using var builder = new AnimationBuilder(world);
        builder.To(target, Value, 10, spec);
        builder.To(target, Other, 20, spec);
        Entity animation = builder.Play();
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        AnimationSystems.Add(systems, spec);
        Tick(systems, world, 0.25);
        uint output = 0;
        world.ExecuteBufferRead<PropertySource<float>, uint>(target, ref output, static (sources, ref output) =>
        {
            foreach (PropertySource<float> source in sources.AsSpan())
                if (source.Channel == Value) output = (uint)source.Track;
        });
        PropertyAnimation.RemoveSource<float>(world, target, animation, output);
        Tick(systems, world, 0.25);
        Require(world.Read<GraphValue>(target).Value == 2.5f && world.Read<GraphValue>(target).Other == 10,
            "Removing a compiled output holds its presented field while the other output keeps sampling.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private struct GraphValue : IComponent { public float Value, Other; }
}
