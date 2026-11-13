using SomeEngine.Animation;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Animation.Consumers;

public readonly record struct StepsParameters(int Steps, string Name);
public struct StepsState { public int Count; public string? Name; }

public readonly struct StepsMotion : IMotion<float, FloatOps, StepsParameters, StepsState>
{
    public static MotionBoundary Describe(in StepsParameters parameters) => new(StageEnd.Settled);
    public static void Initialize(in StepsParameters parameters, in MotionValues<float> values, ref StepsState state) =>
        state = new StepsState { Name = parameters.Name };
    public static void Retarget(in StepsParameters previous, in StepsParameters next, in MotionValues<float> values, ref StepsState state) { }
    public static MotionSample<float> Evaluate(in StepsParameters parameters, in MotionTime time, in MotionValues<float> values, ref StepsState state)
    {
        state.Count++;
        float progress = Math.Min(1, (float)state.Count / parameters.Steps);
        return new(values.From + (values.To - values.From) * progress, state.Count >= parameters.Steps);
    }
}

public readonly record struct AfterSamplesParameters(byte Parameter, float Value, int Samples, string Name);
public struct AfterSamplesState { public int Samples; public string? Name; }

public readonly struct AfterSamplesCondition : IAnimationCondition<AfterSamplesParameters, AfterSamplesState>
{
    public static void Initialize(in AfterSamplesParameters parameters, ref AfterSamplesState state) =>
        state = new AfterSamplesState { Name = parameters.Name };

    public static bool Matches(in AfterSamplesParameters parameters, in GraphParameters inputs, ref AfterSamplesState state)
    {
        state.Samples++;
        return inputs.Values[parameters.Parameter] == parameters.Value && state.Samples >= parameters.Samples;
    }
}

public readonly record struct TimedMarkerParameters(float Duration, int Value);
public struct MarkerValue : IComponent { public int Value; }
public struct MarkerReleases : IComponent { public int Count; }

public struct MarkerState : IBufferElement
{
    public AnimationLeafBatch<TimedMarkerParameters> Definition;
    public int Index;
    public Entity Target;
    public AnimationLeafPlacement Placement;
}

public readonly struct TimedMarkerLeaf : IAnimationLeaf<TimedMarkerParameters>
{
    private static readonly Channel<int> Value = Channel<int>.Of<MarkerValue>(static (ref MarkerValue value) => ref value.Value);
    public static MotionBoundary Describe(in TimedMarkerParameters parameters) => new(StageEnd.Duration, parameters.Duration);
    public static LayerChannel Output(in TimedMarkerParameters parameters) => LayerChannel.Of(Value);

    public static void Release(World world, Entity animation)
    {
        MarkerReleases releases = world.Has<MarkerReleases>(animation) ? world.ReadValue<MarkerReleases>(animation) : default;
        releases.Count++;
        if (world.Has<MarkerReleases>(animation)) world.Set(animation, releases);
        else world.Add(animation, releases);
        {
            using var states = world.GetBuffer<MarkerState>(animation);
states.Clear();

        }
    }

    public static void Install(World world, Entity animation, Entity target, AnimationLeafBatch<TimedMarkerParameters> definitions,
        int index, in AnimationLeafPlacement placement)
    {
        if (!world.Has<Buffer<MarkerState>>(animation))
            world.AddBuffer<MarkerState>(animation);
        var state = new MarkerState { Definition = definitions, Index = index, Target = target, Placement = placement };
        {
            using var states = world.GetBuffer<MarkerState>(animation);
            Visit(states, ref state);

            static void Visit(DynamicBuffer<MarkerState> states, ref MarkerState state)
            {
            if (states.Count > 0 && states[0].Placement.Starts != state.Placement.Starts)
                states.Clear();
            states.Add(state);

            }
        }
    }
}

public struct MarkerSystem : ISystem<ImmediateSystemContext>
{
    private Query _markers;
    public void OnCreate(ref ImmediateSystemContext context) =>
        _markers = context.World.Query(AnimationGraph.WithTimeAccess(new QueryDescription()
            .WithStored<Timeline>().With<Timeline>().WithStored<Buffer<MarkerState>>().Optional<MarkerValue>()));

    public void OnUpdate(ref ImmediateSystemContext context)
    {
        World world = context.World;
        using QueryScope rows = _markers.Open();
        foreach (QueryChunk chunk in rows)
        {
            var times = AnimationGraph.ReadTimeColumn(chunk);
            Span<Timeline> timelines = chunk.Write<Timeline>();
            for (int row = 0; row < chunk.Count; row++)
                foreach (MarkerState marker in chunk.ReadBuffer<MarkerState>(row).AsSpan())
                {
                    AnimationLeafTime time = times.Read(row, in timelines[row], in marker.Placement);
                    if (!time.Active) continue;
                    TimedMarkerParameters parameters = marker.Definition.Parameters[marker.Index];
                    bool finished = time.Time.Time >= parameters.Duration;
                    if (finished) world.Set(marker.Target, new MarkerValue { Value = parameters.Value });
                    times.Submit(row, ref timelines[row], in marker.Placement, !finished);
                }
        }
    }

    public void OnDestroy(ref ImmediateSystemContext context) { }
}
