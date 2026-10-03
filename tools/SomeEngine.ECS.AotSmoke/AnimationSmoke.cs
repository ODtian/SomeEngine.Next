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

    internal static void Check()
    {
        CheckFieldReadsAndStages();
        CheckDefaultFrames();
        CheckCustomValues();
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
        Require(GC.GetAllocatedBytesForCurrentThread() == before,
            "Steady default animation frames allocate zero managed bytes.");
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
