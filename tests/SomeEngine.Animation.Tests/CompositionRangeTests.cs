using System.Runtime.InteropServices;
using SomeEngine.ECS;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Animation.Tests;

public sealed class CompositionRangeTests
{
    private static readonly Channel<TogetherValue> Value = Channel<TogetherValue>.Of<ValueComponent>(
        static (ref ValueComponent component) => ref component.Value);

    [Fact]
    public void IndependentTargetsComposeTogetherBeforeTheirFieldsAreRead()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        using var reached = new CountdownEvent(2);
        GCHandle signal = GCHandle.Alloc(reached);
        try
        {
            var first = world.CreateEntity(new ValueComponent());
            var second = world.CreateEntity(new ValueComponent());
            PropertyAnimation.DeclareContributions<TogetherValue>(world, first);
            PropertyAnimation.DeclareContributions<TogetherValue>(world, second);
            PropertyAnimation.Contribute(world, first, new Contribution<TogetherValue>
            {
                Channel = Value, Source = first, Value = new(3, GCHandle.ToIntPtr(signal)),
            });
            PropertyAnimation.Contribute(world, second, new Contribution<TogetherValue>
            {
                Channel = Value, Source = second, Value = new(7, GCHandle.ToIntPtr(signal)),
            });
            using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
            systems.Add(new ComposeSystem<TogetherValue, TogetherOps>());
            var frame = new ImmediateSystemContext(world, 0, 0);

            systems.Update(ref frame);

            Assert.Equal(3f, world.Read<ValueComponent>(first).Value.Amount);
            Assert.Equal(7f, world.Read<ValueComponent>(second).Value.Amount);
        }
        finally
        {
            signal.Free();
        }
    }

    private readonly record struct TogetherValue(float Amount, nint Signal);

    [Fact]
    public void DeclaredTargetsComposeTogetherInTheCommonAnimationUpdate()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        using var reached = new CountdownEvent(2);
        GCHandle signal = GCHandle.Alloc(reached);
        try
        {
            var layers = new AnimationLayers { Count = 1 };
            layers.Items[0] = new() { Weight = 1 };
            var first = world.CreateEntity(new ValueComponent());
            var second = world.CreateEntity(new ValueComponent());
            world.Add(first, layers);
            world.Add(second, layers);
            var spec = Spec.Create<TogetherValue, TogetherOps, ValueParameters, ValueState, ValueMotion>(new());
            PropertyAnimation.Declare(world, first, Value, spec);
            PropertyAnimation.Declare(world, second, Value, spec);
            PropertyAnimation.To(world, first, Value, new TogetherValue(3, GCHandle.ToIntPtr(signal)), spec);
            PropertyAnimation.To(world, second, Value, new TogetherValue(7, GCHandle.ToIntPtr(signal)), spec);
            using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
            AnimationSystems.Add(systems, spec);
            var frame = new ImmediateSystemContext(world, 0, 0, 0.25);

            systems.Update(ref frame);

            Assert.Equal(3f, world.Read<ValueComponent>(first).Value.Amount);
            Assert.Equal(7f, world.Read<ValueComponent>(second).Value.Amount);
        }
        finally
        {
            signal.Free();
        }
    }

    private struct ValueParameters;
    private struct ValueState;
    private readonly struct ValueMotion : IMotion<TogetherValue, TogetherOps, ValueParameters, ValueState>
    {
        public static MotionBoundary Describe(in ValueParameters parameters) => new(StageEnd.Duration, 1);
        public static void Initialize(in ValueParameters parameters, in MotionValues<TogetherValue> values, ref ValueState state) { }
        public static void Retarget(in ValueParameters previous, in ValueParameters next, in MotionValues<TogetherValue> values, ref ValueState state) { }
        public static MotionSample<TogetherValue> Evaluate(in ValueParameters parameters, in MotionTime time,
            in MotionValues<TogetherValue> values, ref ValueState state) => new(values.To, false);
    }

    private struct ValueComponent : IComponent
    {
        public TogetherValue Value;
    }

    private readonly struct TogetherOps : IValueOps<TogetherValue>
    {
        public static TogetherValue Lerp(in TogetherValue a, in TogetherValue b, float amount)
        {
            var reached = (CountdownEvent)GCHandle.FromIntPtr(b.Signal).Target!;
            reached.Signal();
            if (!reached.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException();
            return new(a.Amount + (b.Amount - a.Amount) * amount, b.Signal);
        }

        public static TogetherValue Add(in TogetherValue a, in TogetherValue b) => new(a.Amount + b.Amount, b.Signal);
        public static TogetherValue Subtract(in TogetherValue a, in TogetherValue b) => new(a.Amount - b.Amount, b.Signal);
        public static TogetherValue Scale(in TogetherValue value, float amount) => new(value.Amount * amount, value.Signal);
        public static float Distance(in TogetherValue a, in TogetherValue b) => Math.Abs(a.Amount - b.Amount);
    }
}
