using SomeEngine.Animation;
using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
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

[BufferCapacity(1)]
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

    public static void Install(World world, Entity animation, Entity target, AnimationLeafBatch<TimedMarkerParameters> definitions,
        int index, in AnimationLeafPlacement placement)
    {
        if (!world.HasBuffer<MarkerState>(animation))
            world.AddBuffer<MarkerState>(animation);
        var state = new MarkerState { Definition = definitions, Index = index, Target = target, Placement = placement };
        world.ExecuteBufferWrite<MarkerState, MarkerState>(animation, ref state, static (states, ref state) =>
        {
            if (states.Count > 0 && states.Read(0).Placement.Starts != state.Placement.Starts)
                states.Clear();
            states.Add(state);
        });
    }
}

public struct MarkerSystem : ISystem<ImmediateSystemContext>
{
    private QueryHandle _markers;
    public void OnCreate(ref ImmediateSystemContext context) =>
        _markers = context.World.Query(context.World.QueryDefinition().ReadWrite<Timeline>().Enabled<Timeline>()
            .ReadBuffer<MarkerState>().Optional<MarkerValue>(QueryAccess.ReadWrite).OptionalBuffer<TimelineStage>(QueryAccess.Read));

    public void OnUpdate(ref ImmediateSystemContext context)
    {
        World world = context.World;
        world.ExecuteQuery(_markers, ref world, static (QueryCursor rows, ref World world) =>
        {
            foreach (QueryRow row in rows.Rows)
                foreach (MarkerState marker in row.ReadBuffer<MarkerState>().AsSpan())
                {
                    AnimationLeafTime time = AnimationGraph.ReadTime(world, row.Entity, in marker.Placement);
                    if (!time.Active)
                        continue;
                    TimedMarkerParameters parameters = marker.Definition.Parameters[marker.Index];
                    bool finished = time.Time.Time >= parameters.Duration;
                    if (finished)
                        world.Replace(marker.Target, new MarkerValue { Value = parameters.Value });
                    AnimationGraph.Submit(world, row.Entity, in marker.Placement, !finished);
                }
        });
    }

    public void OnDestroy(ref ImmediateSystemContext context) => context.World.ReleaseQuery(_markers);
}
