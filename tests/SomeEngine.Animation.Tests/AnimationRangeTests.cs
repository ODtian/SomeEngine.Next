using SomeEngine.ECS;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Animation.Tests;

public sealed class AnimationRangeTests
{
    [Fact]
    public void IndependentMotionInstancesEvaluateTogetherBeforeTimelineCompletion()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        using var reached = new CountdownEvent(2);
        var spec = Spec.Create<float, FloatOps, TogetherParameters, TogetherState, TogetherMotion>(new(reached));
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        AnimationSystems.Add(systems, spec);
        var first = world.CreateEntity(new FieldPanel());
        var second = world.CreateEntity(new FieldPanel());
        PropertyAnimation.Declare(world, first, FieldPanelChannels.Opacity, spec);
        PropertyAnimation.Declare(world, second, FieldPanelChannels.Opacity, spec);
        PropertyAnimation.To(world, first, FieldPanelChannels.Opacity, 3f, spec);
        PropertyAnimation.To(world, second, FieldPanelChannels.Opacity, 7f, spec);

        var frame = new ImmediateSystemContext(world, 0, 0, 0.25);
        systems.Update(ref frame);

        Assert.Equal(3f, world.Read<FieldPanel>(first).Opacity);
        Assert.Equal(7f, world.Read<FieldPanel>(second).Opacity);
        Assert.Equal(1u, world.Read<Timeline>(first).Completions);
        Assert.Equal(1u, world.Read<Timeline>(second).Completions);
    }

    private readonly record struct TogetherParameters(CountdownEvent Reached);
    private struct TogetherState;
    private readonly struct TogetherMotion : IMotion<float, FloatOps, TogetherParameters, TogetherState>
    {
        public static MotionBoundary Describe(in TogetherParameters parameters) => new(StageEnd.Settled);
        public static void Initialize(in TogetherParameters parameters, in MotionValues<float> values, ref TogetherState state) { }
        public static void Retarget(in TogetherParameters previous, in TogetherParameters next, in MotionValues<float> values, ref TogetherState state) { }
        public static MotionSample<float> Evaluate(in TogetherParameters parameters, in MotionTime time, in MotionValues<float> values, ref TogetherState state)
        {
            parameters.Reached.Signal();
            if (!parameters.Reached.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException();
            return new(values.To, true);
        }
    }
}
