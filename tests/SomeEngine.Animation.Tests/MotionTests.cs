using SomeEngine.Animation.Consumers;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Animation.Tests;

public class MotionTests
{
    [Fact]
    public void ConsumerMotionAddedAfterDefaultsRunsInTheCommonEvaluationStage()
    {
        using var world = new World();
        Entity target = world.CreateEntity(new MotionTarget());
        Channel<float> channel = Channel<float>.Of<MotionTarget>(static (ref MotionTarget value) => ref value.Value);
        var spec = Spec.Create<float, FloatOps, HistoryParameters, HistoryState, HistoryMotion>(new(1, 0.5f, "path"));
        PropertyAnimation.Declare(world, target, channel, spec);
        PropertyAnimation.To(world, target, channel, 10, spec);
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        AnimationSystems.AddDefaults(systems);
        AnimationSystems.Add(systems, spec);
        var frame = new ImmediateSystemContext(world, 0, 0, 0.5);

        systems.Update(ref frame);

        Assert.Equal(2.5f, world.Read<MotionTarget>(target).Value);
    }

    [Fact]
    public void BatchUsesEachElementsOwnTimeAndReportsCompletion()
    {
        var parameters = new[] { new TweenParameters(1) };
        var states = new TweenState[3];
        var values = new[] { new MotionValues<float>(0, 10, 0), new(0, 10, 0), new(0, 10, 0) };
        var times = new[] { new MotionTime(0.25f, 0.25f), new(0.5f, 0.5f), new(1, 1) };
        var samples = new MotionSample<float>[3];
        var batch = new MotionBatch<float, TweenParameters, TweenState>(parameters, states, values, times, samples);

        MotionDispatcher<float, FloatOps, TweenParameters, TweenState, Tween<float, FloatOps, Linear>>.EvaluateBatch(ref batch);

        Assert.Equal(new[] { new MotionSample<float>(2.5f, false), new(5, false), new(10, true) }, samples);
    }

    [Fact]
    public void ConsumerEasingAndValueOperationsDetermineTheSample()
    {
        var spec = Spec.Tween<Distance, DistanceOps, SquareEasing>(1);
        var state = new TweenState();
        var values = new MotionValues<Distance>(new(0), new(16), new(0));
        var time = new MotionTime(0.5f, 0.5f);

        MotionSample<Distance> sample = Tween<Distance, DistanceOps, SquareEasing>.Evaluate(spec.Parameters, in time, in values, ref state);

        Assert.Equal(new Distance(4), sample.Value);
    }

    [Fact]
    public void ManagedConsumerStateKeepsItsHistoryAcrossParameterChangesAndRetargets()
    {
        var first = new HistoryParameters(1, 1, "first");
        var next = new HistoryParameters(2, 0.5f, "next");
        var state = new HistoryState();
        var values = new MotionValues<float>(0, 10, 0);
        var time = new MotionTime(0.25f, 0.25f);
        HistoryMotion.Initialize(in first, in values, ref state);
        HistoryMotion.Evaluate(in first, in time, in values, ref state);

        HistoryMotion.UpdateParameters(in first, in next, ref state);
        HistoryMotion.Retarget(in first, in next, in values, ref state);
        HistoryMotion.Evaluate(in next, in time, in values, ref state);

        Assert.Equal(("next", 2, 1, 1), (state.Name, state.Samples, state.Updates, state.Retargets));
    }

    [Fact]
    public void ConsumerWholeBatchProducesTheSameSamplesAsTheDefaultBatch()
    {
        var first = new[]
        {
            new MotionData<float, HistoryParameters, HistoryState>
            {
                Active = true, Parameters = new(1, 0.5f, "path"), Values = new(2, 10, 2), Time = new(0.5f, 0.5f),
            },
        };
        var second = first.ToArray();
        var ordinary = new MotionBatch<float, HistoryParameters, HistoryState>(first);
        var specialized = new MotionBatch<float, HistoryParameters, HistoryState>(second);

        MotionDispatcher<float, FloatOps, HistoryParameters, HistoryState, HistoryMotion>.EvaluateBatch(ref ordinary);
        MotionDispatcher<float, FloatOps, HistoryParameters, HistoryState, HistoryBatchMotion>.EvaluateBatch(ref specialized);

        Assert.Equal(first[0].Sample, second[0].Sample);
    }

    [Fact]
    public void SpringRetargetCarriesTheExistingVelocity()
    {
        var parameters = new SpringParameters(180, 20);
        var values = new MotionValues<float>(0, 1, 0);
        var time = new MotionTime(1f / 60, 1f / 60);
        var state = new SpringState<float>();
        MotionSample<float> sample = Spring<float, FloatOps>.Evaluate(in parameters, in time, in values, ref state);
        float velocity = state.Velocity;
        values = new(sample.Value, 2, sample.Value);

        Spring<float, FloatOps>.Retarget(in parameters, in parameters, in values, ref state);

        Assert.Equal(velocity, state.Velocity);
    }

    [Fact]
    public void ConsumerSpringMatchesTheBuiltInSpringFormula()
    {
        var parameters = new SpringParameters(180, 20);
        var values = new MotionValues<float>(0, 1, 0);
        var time = new MotionTime(1f / 60, 1f / 60);
        var first = new SpringState<float>();
        var second = new SpringState<float>();

        MotionSample<float> builtIn = Spring<float, FloatOps>.Evaluate(in parameters, in time, in values, ref first);
        MotionSample<float> consumer = ConsumerSpring.Evaluate(in parameters, in time, in values, ref second);

        Assert.Equal(builtIn, consumer);
    }

    [Fact]
    public void KeyframesReadTheSharedDefinitionAtTheCurrentInterval()
    {
        var spec = Spec.Keyframes<float, FloatOps>(1, new Keyframe[] { new(0, 0), new(0.5f, 0.25f), new(1, 1) });
        var values = new MotionValues<float>(0, 10, 0);
        var state = new KeyframeState();
        var time = new MotionTime(0.75f, 0.75f);

        MotionSample<float> sample = Keyframes<float, FloatOps>.Evaluate(spec.Parameters, in time, in values, ref state);

        Assert.Equal(6.25f, sample.Value);
    }

    [Fact]
    public void ReleaseAllowsConsumerStateToBeInitializedForAnotherDefinition()
    {
        var first = new HistoryParameters(1, 1, "first");
        var next = new HistoryParameters(1, 1, "next");
        var state = new HistoryState();
        var values = new MotionValues<float>(0, 10, 0);
        HistoryMotion.Initialize(in first, in values, ref state);
        var time = new MotionTime(1, 1);
        HistoryMotion.Evaluate(in first, in time, in values, ref state);

        HistoryMotion.Release(ref state);
        HistoryMotion.Initialize(in next, in values, ref state);

        Assert.Equal(("next", 0), (state.Name, state.Samples));
    }

    private struct MotionTarget : IComponent
    {
        public float Value;
    }
}
