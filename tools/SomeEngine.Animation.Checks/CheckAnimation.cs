using SomeEngine.Animation;
using SomeEngine.Animation.Consumers;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Animation.Checks;

internal static class CheckAnimation
{
    public static int Main(string[] args)
    {
        if (args.Contains("--benchmark"))
        {
            BatchMeasurements.Run();
            return 0;
        }
        CheckTypedBatch();
        CheckManagedHistory();
        CheckSpring();
        CheckCustomValue();
        CheckFunction();
        CheckClocksAndCompletion();
        Console.WriteLine("ANIMATION_CHECKS_PASSED typed-batch managed-history spring custom-value function clocks completion");
        return 0;
    }

    private static void CheckTypedBatch()
    {
        var data = new[]
        {
            new MotionData<float, TweenParameters, TweenState>
            {
                Active = true, Parameters = new(1), Values = new(0, 10, 0), Time = new(0.25f, 0.25f),
            },
            new MotionData<float, TweenParameters, TweenState>
            {
                Active = true, Parameters = new(1), Values = new(0, 10, 0), Time = new(1, 1),
            },
        };
        var batch = new MotionBatch<float, TweenParameters, TweenState>(data);
        MotionDispatcher<float, FloatOps, TweenParameters, TweenState, Tween<float, FloatOps, Linear>>.EvaluateBatch(ref batch);
        Require(data[0].Sample == new MotionSample<float>(2.5f, false) && data[1].Sample == new MotionSample<float>(10, true),
            "Each batch element uses its own time and reports its completion.");
    }

    private static void CheckManagedHistory()
    {
        var parameters = new HistoryParameters(1, 1, "first");
        var values = new MotionValues<float>(0, 10, 0);
        var state = new HistoryState();
        HistoryMotion.Initialize(in parameters, in values, ref state);
        var time = new MotionTime(0.5f, 0.5f);
        Require(HistoryMotion.Evaluate(in parameters, in time, in values, ref state).Value == 5, "Consumer history produces its value.");
        var next = new HistoryParameters(2, 0.5f, "next");
        HistoryMotion.UpdateParameters(in parameters, in next, ref state);
        HistoryMotion.Retarget(in parameters, in next, in values, ref state);
        Require(state.Name == "next" && state.Samples == 1 && state.Updates == 1 && state.Retargets == 1,
            "Managed state retains its history through updates and retargets.");
        HistoryMotion.Release(ref state);
        HistoryMotion.Initialize(in next, in values, ref state);
        Require(state.Name == "next" && state.Samples == 0, "Released history is initialized for another definition.");
    }

    private static void CheckSpring()
    {
        var parameters = new SpringParameters(180, 20);
        var values = new MotionValues<float>(0, 1, 0);
        var state = new SpringState<float>();
        var consumerState = new SpringState<float>();
        var time = new MotionTime(1f / 60, 1f / 60);
        MotionSample<float> sample = Spring<float, FloatOps>.Evaluate(in parameters, in time, in values, ref state);
        MotionSample<float> consumer = ConsumerSpring.Evaluate(in parameters, in time, in values, ref consumerState);
        Require(sample == consumer && state.Velocity == consumerState.Velocity, "Consumer and built-in springs use the same public formula.");
        float velocity = state.Velocity;
        values = new(sample.Value, 2, sample.Value);
        Spring<float, FloatOps>.Retarget(in parameters, in parameters, in values, ref state);
        Require(state.Velocity == velocity, "Spring retargeting carries velocity.");
        for (int frame = 0; frame < 600; frame++)
        {
            sample = Spring<float, FloatOps>.Evaluate(in parameters, in time, in values, ref state);
            values = new(values.From, values.To, sample.Value);
            if (sample.Finished)
                break;
        }
        Require(sample.Finished && sample.Value == 2, "The spring reports settlement at its target.");
    }

    private static void CheckCustomValue()
    {
        var parameters = new TweenParameters(1);
        var values = new MotionValues<Distance>(new(0), new(16), new(0));
        var time = new MotionTime(0.5f, 0.5f);
        var state = new TweenState();
        MotionSample<Distance> sample = Tween<Distance, DistanceOps, SquareEasing>.Evaluate(in parameters, in time, in values, ref state);
        Require(sample.Value == new Distance(4), "Consumer values and easing determine the result.");
        Require(ValueOps<Distance, DistanceOps>.Equals(sample.Value, new Distance(4)), "Custom leaf equality is available through the common value API.");
    }

    private static unsafe void CheckFunction()
    {
        var spec = Spec.FromFunction<float, FloatOps>(1, &Square);
        var values = new MotionValues<float>(0, 16, 0);
        var time = new MotionTime(0.5f, 0.5f);
        var state = new TweenState();
        MotionSample<float> sample = FunctionTween<float, FloatOps>.Evaluate(spec.Parameters, in time, in values, ref state);
        Require(sample.Value == 4, "A scalar function address adapts to the typed motion lifecycle.");
    }

    private static float Square(float progress) => progress * progress;

    private static void CheckClocksAndCompletion()
    {
        using var world = new World();
        Entity time = world.CreateEntity(new AnimationTime(0.25f, 0.5f, gamePaused: true));
        Entity game = world.CreateEntity(new Timeline { Clock = TimelineClock.Game, Speed = 2 });
        Entity screen = world.CreateEntity(new Timeline { Clock = TimelineClock.Interface, Speed = 2 });
        Entity unscaled = world.CreateEntity(new Timeline { Clock = TimelineClock.Unscaled, Speed = 2 });
        using var clocks = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        clocks.Add(new TimelineSystem(time));
        clocks.Update();
        Require(world.Read<Timeline>(game).Time == 0 && world.Read<Timeline>(screen).Time == 0.5f && world.Read<Timeline>(unscaled).Time == 0,
            "Clock deltas apply game pause, interface time, and individual speed.");

        Entity animation = world.CreateEntity(new Timeline());
        world.AddBuffer<TimelineStage>(animation);
        world.AddBuffer<TimelineEvent>(animation);
        world.ExecuteBufferWrite<TimelineStage>(animation, static stages =>
        {
            stages.Add(new TimelineStage { End = StageEnd.Duration, Duration = 0.1f });
            stages.Add(new TimelineStage { End = StageEnd.External });
        });
        using var stages = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        stages.Add(new TimelineSystem());
        stages.Add(new TimelineEndSystem());
        var context = new ImmediateSystemContext(world, 0, 0, 0.25);
        stages.Update(ref context);
        Require(world.Read<Timeline>(animation).Stage == 1 && MathF.Abs(world.Read<Timeline>(animation).Time - 0.15f) < 0.00001f,
            "Duration boundaries carry excess time into the external stage.");
        Animation.Advance(world, animation);
        stages.Update(ref context);
        Require(!world.IsEnabled<Timeline>(animation) && world.Read<Timeline>(animation).Completions == 1,
            "External advancement reaches common completion.");
        Animation.EmitEvent(world, animation, 5);
        Animation.EmitEvent(world, animation, 5);
        world.ExecuteBufferRead<TimelineEvent>(animation, static events => Require(events[0].Count == 2, "Events accumulate counts."));
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
