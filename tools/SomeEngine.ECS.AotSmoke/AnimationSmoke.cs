using System.Numerics;
using SomeEngine.Animation;
using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Tools.EcsAotSmoke;

internal static class AnimationSmoke
{
    private static readonly Channel<float> Opacity = Channel<float>.Of<Visual>(static (ref Visual value) => ref value.Opacity);
    private static readonly Channel<Vector2> Position = Channel<Vector2>.Of<Visual>(static (ref Visual value) => ref value.Position);
    private static readonly Channel<float> HoverWeight = Channel<float>.Of<AnimationLayers>(
        static (ref AnimationLayers layers) => ref layers.Items[1].Weight);

    internal static void Check()
    {
        CheckFieldReadsAndStages();
        CheckDefaultFrames();
        CheckCustomValues();
        foreach (bool inline in new[] { false, true })
            foreach (bool buffered in new[] { false, true })
                CheckLayerValues(inline, buffered);
        CheckGraphs();
    }

    private static void CheckFieldReadsAndStages()
    {
        using var world = new World();
        Entity target = world.CreateEntity(new Visual { Opacity = 0.25f });
        PropertyAnimation.Declare<float>(world, target);
        PropertyAnimation.Declare<Vector2>(world, target);
        uint version = world.WriteVersion<Visual>(target);
        PropertyAnimation.To(world, target, Opacity, 1f, Spec.Tween(1));
        PropertyAnimation.Pause(world, target);
        PropertyAnimation.Retarget(world, target, Opacity, 2f);
        Require(world.Read<Visual>(target).Opacity == 0.25f && world.WriteVersion<Visual>(target) == version,
            "Starting and retargeting preserve the presented field and its write version.");
        PropertyAnimation.Resume(world, target);
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        AnimationSystems.AddDefaults(systems);
        Tick(systems, world, 0.5);
        Require(world.Read<Visual>(target).Opacity == 1.125f && world.WriteVersion<Visual>(target) != version,
            "Sampling starts at the presented value and advances its write version.");

        PropertyAnimation.Stop(world, target);
        world.Replace(target, new Visual());
        world.AddBuffer<TimelineStage>(target);
        world.ExecuteBufferWrite<TimelineStage>(target, static stages =>
        {
            stages.Add(new TimelineStage { End = StageEnd.Duration, Duration = 0.1f });
            stages.Add(new TimelineStage { End = StageEnd.Duration, Duration = 0.1f });
        });
        PropertyAnimation.To(world, target, Opacity, 1f, Spec.Tween(0.1f));
        PropertyAnimation.To(world, target, Position, new Vector2(1, 2), Spec.Tween(0.1f));
        uint starts = world.Read<Timeline>(target).Starts;
        var scalar = new Track<float>
        {
            Channel = Opacity, From = 1, Current = 1, To = 2, Duration = 0.1f,
            Curve = Spec.Tween(0.1f).Curve, Stage = 1, StartId = starts,
        };
        world.ExecuteBufferWrite<Track<float>, Track<float>>(target, ref scalar,
            static (DynamicBuffer<Track<float>> tracks, ref Track<float> track) => tracks.Add(track));
        var vector = new Track<Vector2>
        {
            Channel = Position, From = new Vector2(1, 2), Current = new Vector2(1, 2), To = new Vector2(2, 4),
            Duration = 0.1f, Curve = Spec.Tween(0.1f).Curve, Stage = 1, StartId = starts,
        };
        world.ExecuteBufferWrite<Track<Vector2>, Track<Vector2>>(target, ref vector,
            static (DynamicBuffer<Track<Vector2>> tracks, ref Track<Vector2> track) => tracks.Add(track));
        Tick(systems, world, 0.25);
        Visual result = world.Read<Visual>(target);
        Require(result.Opacity == 2 && result.Position == new Vector2(2, 4) && !world.IsEnabled<Timeline>(target),
            "Crossed duration stages present the final values of every track type before completion.");
    }

    private static void CheckLayerValues(bool inline, bool buffered)
    {
        using var world = new World();
        Entity target = world.CreateEntity(new Visual { Opacity = 10 });
        var layers = new AnimationLayers { Count = 2 };
        layers.Items[0] = new AnimationLayer { Weight = 1 };
        layers.Items[1] = new AnimationLayer { Mode = LayerMode.Additive, Weight = 0.25f };
        world.Add(target, layers);
        PropertyAnimation.SetBase(world, target, Opacity, 10f);
        if (inline)
            PropertyAnimation.Declare<float>(world, target);
        if (buffered)
            world.AddBuffer<Contribution<float>>(target);
        PropertyAnimation.To(world, target, Opacity, 16f, Spec.Tween(0.25f), layer: 1);
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        AnimationSystems.AddDefaults(systems);
        Tick(systems, world, 0.25);
        Require(world.Read<Visual>(target).Opacity == 11.5f, "The finished value blends with its initial layer weight.");
        PropertyAnimation.To(world, target, HoverWeight, 1f, Spec.Tween(0.75f));
        Tick(systems, world, 0.25);
        Require(world.Read<Visual>(target).Opacity == 13, "A later weight animation continues blending the finished value.");
        Tick(systems, world, 0.5);
        Require(world.Read<Visual>(target).Opacity == 16, "The finished value reaches full layer weight.");
        PropertyAnimation.To(world, target, Opacity, 20f, Spec.Tween(1), layer: 1);
        Tick(systems, world, 0);
        Require(world.Read<Visual>(target).Opacity == 16, "A completed layer starts a new To continuously.");
        Tick(systems, world, 0.5);
        Require(world.Read<Visual>(target).Opacity == 18, "The new To advances from the completed layer value.");
        Tick(systems, world, 0.5);
        layers.Items[1].Weight = 0.5f;
        world.Replace(target, layers);
        Tick(systems, world, 0);
        Require(world.Read<Visual>(target).Opacity == 15, "Completed values keep responding to explicit layer weight changes.");
        PropertyAnimation.To(world, target, Opacity, 24f, Spec.Tween(1), layer: 1);
        Tick(systems, world, 0);
        Require(world.Read<Visual>(target).Opacity == 15, "Partial layer weight preserves the new To's initial presented value.");
        Tick(systems, world, 0.5);
        Require(world.Read<Visual>(target).Opacity == 16, "Partial layer weight preserves the new To's continuous progress.");
    }

    private static void CheckDefaultFrames()
    {
        using var world = new World();
        Entity inline = world.CreateEntity(new Visual());
        Entity ordinary = world.CreateEntity(new Visual());
        PropertyAnimation.Declare<float>(world, inline);
        PropertyAnimation.To(world, inline, Opacity, 1f, Spec.Tween(10000));
        Entity pooled = PropertyAnimation.To(world, ordinary, Opacity, 1f, Spec.Tween(10000));
        int entities = world.EntityCount;
        using var tables = world.ObserveEntityTables();
        tables.Accept(tables.Read());
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        AnimationSystems.AddDefaults(systems);
        for (int frame = 0; frame < 512; frame++) Tick(systems, world, 1d / 60);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int frame = 0; frame < 128; frame++) Tick(systems, world, 1d / 60);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Require(allocated == 0,
            $"Steady default animation frames allocate zero managed bytes; observed {allocated} bytes.");
        Require(world.Read<Visual>(inline).Opacity > 0 && world.Read<Visual>(inline).Opacity == world.Read<Visual>(ordinary).Opacity,
            "Inline and pooled targets advance through the same default systems.");
        Tick(systems, world, 10000);
        Require(!world.IsEnabled<Timeline>(pooled), "Completed pooled animations release their enabled time line.");
        Require(PropertyAnimation.To(world, ordinary, Opacity, 2f, Spec.Tween(1)) == pooled,
            "Later animations reuse the completed pooled entity.");
        Tick(systems, world, 1);
        Require(world.Read<Visual>(ordinary).Opacity == 2 && world.EntityCount == entities && tables.Read().Count == 0,
            "Reusing animation storage preserves entity count and table membership.");
    }

    private static void CheckCustomValues()
    {
        using var world = new World();
        Entity target = world.CreateEntity(new Reading { Value = new Meter(10) });
        Channel<Meter> channel = Channel<Meter>.Of<Reading>(static (ref Reading reading) => ref reading.Value);
        PropertyAnimation.Declare<Meter>(world, target);
        PropertyAnimation.To(world, target, channel, new Meter(20), Spec.Tween(1));
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        systems.Add(new TimelineSystem());
        systems.Add(new TrackSystem<Meter, MeterOps>());
        systems.Add(new TimelineEndSystem());
        Tick(systems, world, 0.5);
        Require(world.Read<Reading>(target).Value == new Meter(15), "Custom value operations sample a tween.");
        PropertyAnimation.Retarget(world, target, channel, new Meter(30));
        Tick(systems, world, 1);
        Require(world.Read<Reading>(target).Value == new Meter(30), "Custom value tracks support retargeting.");
        PropertyAnimation.To(world, target, channel, new Meter(40), Spec.Spring(tolerance: 0.0001f));
        for (int frame = 0; frame < 1200 && world.IsEnabled<Timeline>(target); frame++) Tick(systems, world, 1d / 60);
        Require(world.Read<Reading>(target).Value == new Meter(40) && !world.IsEnabled<Timeline>(target),
            "Custom value operations settle a spring at its target.");
    }

    private static void CheckGraphs()
    {
        using var world = new World();
        Entity first = world.CreateEntity(new Visual());
        Entity second = world.CreateEntity(new Visual());
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        AnimationSystems.AddDefaults(systems);
        using var graph = new AnimationBuilder(world);
        using (graph.Sequence())
        {
            graph.To(first, Opacity, 1f, Spec.Tween(0.1f));
            graph.Delay(0.1f);
            graph.To(second, Position, new Vector2(2, 4), Spec.Tween(0.1f));
        }
        Entity animation = graph.Play();
        Tick(systems, world, 0.3);
        Require(world.Read<Visual>(first).Opacity == 1 && world.Read<Visual>(second).Position == new Vector2(2, 4),
            "Fused graphs sample closed generic track installers and all value types.");
        Require(!world.IsEnabled<Timeline>(animation), "Fused graph time completes after its final interval.");
        var parameters = new GraphParameters();
        using (var machine = graph.StateMachine(ref parameters))
        {
            using (machine.State(0)) graph.To(first, Opacity, 0f, Spec.Tween(0));
            using (machine.State(1)) graph.To(first, Opacity, 2f, Spec.Tween(0));
            machine.Transition(0, 1, GraphCondition.Equal(0, 1), 0.2f);
        }
        CompiledGraph compiled = graph.Compile();
        Entity a = compiled.Play(world, first);
        compiled.Play(world, second);
        compiled.Dispose();
        Tick(systems, world, 0);
        parameters.Values[0] = 1;
        world.Replace(a, parameters);
        Tick(systems, world, 0.1);
        Require(world.Read<Visual>(first).Opacity == 1 && world.Read<Visual>(second).Opacity == 0,
            "Shared compiled state graphs retain data and advance their own parameters and fade weights.");
    }

    private static void Tick(SystemGroup<ImmediateSystemContext> systems, World world, double delta)
    {
        var context = new ImmediateSystemContext(world, 0, 0, delta);
        systems.Update(ref context);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private struct Visual : IComponent
    {
        public float Opacity;
        public Vector2 Position;
    }

    private struct Reading : IComponent
    {
        public Meter Value;
    }

    private readonly record struct Meter(float Value);
    private readonly struct MeterOps : IValueOps<Meter>
    {
        public static Meter Lerp(in Meter a, in Meter b, float t) => new(a.Value + (b.Value - a.Value) * t);
        public static Meter Add(in Meter a, in Meter b) => new(a.Value + b.Value);
        public static Meter Subtract(in Meter a, in Meter b) => new(a.Value - b.Value);
        public static Meter Scale(in Meter a, float scale) => new(a.Value * scale);
        public static float Distance(in Meter a, in Meter b) => MathF.Abs(a.Value - b.Value);
    }
}
