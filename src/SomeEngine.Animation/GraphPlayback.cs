using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;

namespace SomeEngine.Animation;

internal struct GraphInstance : ICleanupComponent
{
    public GraphData[]? Data;
    public int Part;
    public Entity TimeSource;
    public bool CleanupQueued;
}

[BufferCapacity(4)]
internal struct GraphState : IBufferElement
{
    public int Current, CurrentMotion;
    public float Time, FadeTime, FadeDuration;
    public bool Started;
}

[BufferCapacity(4)]
internal struct GraphMotion : IBufferElement
{
    public float Time, Weight, BlendWeight, FromWeight;
    public ushort Stage;
    public uint Starts;
    public bool Running;
}

internal static class GraphPlayback
{
    public static void Start(World world, Entity animation, GraphData[] data, int part, Entity timeSource)
    {
        if (world.Has<GraphInstance>(animation))
        {
            GraphInstance previous = world.Read<GraphInstance>(animation);
            if (previous.Data is not null)
            {
                ReleaseConditions(world, animation, previous.Data);
                GraphData.Release(previous.Data);
            }
            world.Replace(animation, new GraphInstance());
        }
        if (data[0].GroupCount == 0 && data[0].PartCount == 1)
            return;
        Interlocked.Increment(ref data[0].References);
        var instance = new GraphInstance { Data = data, Part = part, TimeSource = timeSource };
        if (world.Has<GraphInstance>(animation))
            world.Replace(animation, instance);
        else
            world.Add(animation, instance);
        for (int index = 0; index < data[0].NodeCount; index++)
        {
            GraphNode node = data[0].Nodes![index];
            node.ConditionData?.Initialize(world, animation, node.ConditionIndex);
        }
        if (data[0].GroupCount == 0)
            return;
        if (!world.Has<GraphParameters>(animation))
            world.Add(animation, data[0].Parameters);
        else
            world.Replace(animation, data[0].Parameters);
        if (!world.HasBuffer<GraphState>(animation))
            world.AddBuffer<GraphState>(animation);
        if (!world.HasBuffer<GraphMotion>(animation))
            world.AddBuffer<GraphMotion>(animation);
        world.ExecuteBufferWrite<GraphState, int>(animation, ref data[0].GroupCount,
            static (DynamicBuffer<GraphState> states, ref int count) =>
            {
                states.Clear();
                for (int i = 0; i < count; i++)
                    states.Add(default);
            });
        world.ExecuteBufferWrite<GraphMotion, int>(animation, ref data[0].MotionCount,
            static (DynamicBuffer<GraphMotion> motions, ref int count) =>
            {
                motions.Clear();
                for (int i = 0; i < count; i++)
                    motions.Add(default);
            });
    }

    public static unsafe void Update(World world, Entity animation, GraphData[] graph, int part,
        in Timeline timeline, in GraphParameters parameters, DynamicBuffer<GraphState> states, DynamicBuffer<GraphMotion> motions)
    {
        ref GraphData data = ref graph[0];
        GraphPart phases = data.Parts![part];
        var graphTime = GraphTimes.Project(phases.Stages.AsSpan(0, phases.StageCount), timeline.Stage, timeline.Time);
        for (int groupIndex = phases.GroupFirst; groupIndex < phases.GroupFirst + phases.GroupCount; groupIndex++)
        {
            int g = data.PartGroups![groupIndex];
            GraphGroupData group = data.Groups![g];
            var time = graphTime;
            float groupWeight = 1;
            if (group.ParentMotion > 0)
            {
                GraphMotion parent = motions.Read(group.ParentMotion - 1);
                time = (parent.Stage, parent.Time);
                groupWeight = parent.Weight;
            }
            bool active = time.Stage == group.Stage && time.Time >= group.Start && groupWeight > 0;
            ref GraphState state = ref states[g];
            if (!active)
            {
                for (int index = group.MotionFirst; index < group.MotionFirst + group.MotionCount; index++)
                    motions[data.GroupMotions![index]].Weight = 0;
                continue;
            }
            if (!state.Started)
            {
                int first = data.GroupMotions![group.MotionFirst];
                for (int index = group.MotionFirst; index < group.MotionFirst + group.MotionCount; index++)
                {
                    int motion = data.GroupMotions[index];
                    if (data.Nodes![data.Motions![motion].Node].Initial)
                        first = motion;
                }
                state.Current = data.Nodes![data.Motions![first].Node].StateId;
                state.CurrentMotion = first;
                motions[first].BlendWeight = 1;
                motions[first].Starts++;
                state.Started = true;
            }
            GraphNode definition = data.Nodes![group.Node];
            float elapsed = time.Time - group.Start;
            float delta = Math.Max(0, elapsed - state.Time);
            state.Time = elapsed;
            if (definition.Kind == GraphNodeKind.Blend1D)
            {
                UpdateBlend(in data, in group, parameters.Values[definition.Parameter], delta, groupWeight, timeline.Paused, motions);
                continue;
            }
            if (definition.Kind is GraphNodeKind.Parallel or GraphNodeKind.Stagger)
            {
                for (int index = group.MotionFirst; index < group.MotionFirst + group.MotionCount; index++)
                {
                    ref GraphMotion motion = ref motions[data.GroupMotions![index]];
                    motion.Starts = 1;
                    motion.Weight = groupWeight;
                    if (!timeline.Paused)
                        motion.Time += delta;
                    motion.Running = false;
                }
                continue;
            }
            if (!timeline.Paused)
            {
                GraphMotionData current = data.Motions![state.CurrentMotion];
                for (int candidate = current.TransitionFirst; candidate < current.TransitionFirst + current.TransitionCount; candidate++)
                {
                    GraphNode transition = data.Nodes[data.TransitionNodes![candidate]];
                    if (!transition.Matches(world, animation, in transition, in parameters))
                        continue;
                    state.Current = transition.NextState;
                    state.FadeTime = 0;
                    state.FadeDuration = transition.FadeDuration;
                    for (int index = group.MotionFirst; index < group.MotionFirst + group.MotionCount; index++)
                    {
                        int m = data.GroupMotions![index];
                        ref GraphMotion motion = ref motions[m];
                        motion.FromWeight = motion.BlendWeight;
                        if (data.Nodes[data.Motions[m].Node].StateId == state.Current)
                        {
                            state.CurrentMotion = m;
                            motion.Starts++;
                            motion.Time = 0;
                            motion.Stage = 0;
                        }
                    }
                    break;
                }
                state.FadeTime += delta;
            }
            float progress = state.FadeDuration == 0 ? 1 : Math.Clamp(state.FadeTime / state.FadeDuration, 0, 1);
            for (int index = group.MotionFirst; index < group.MotionFirst + group.MotionCount; index++)
            {
                int m = data.GroupMotions![index];
                ref GraphMotion motion = ref motions[m];
                float destination = data.Nodes[data.Motions![m].Node].StateId == state.Current ? 1 : 0;
                motion.BlendWeight = motion.FromWeight + (destination - motion.FromWeight) * progress;
                motion.Weight = motion.BlendWeight * groupWeight;
                if (!timeline.Paused && motion.Weight > 0)
                    motion.Time += delta;
                motion.Running = false;
            }
        }
    }

    internal static void ReleaseConditions(World world, Entity animation, GraphData[] data)
    {
        for (int index = 0; index < data[0].NodeCount; index++)
        {
            GraphNode node = data[0].Nodes![index];
            node.ConditionData?.Release(world, animation, node.ConditionIndex);
        }
    }

    private static void UpdateBlend(in GraphData data, in GraphGroupData group, float coordinate, float delta,
        float groupWeight, bool paused, DynamicBuffer<GraphMotion> motions)
    {
        SomeEngine.Core.Diagnostics.DevelopmentCheck.Require(float.IsFinite(coordinate), "A blend coordinate must be finite.");
        int first = group.BlendFirst, last = first + group.BlendCount - 1;
        int low = first, high = last;
        while (low <= high)
        {
            int middle = low + (high - low) / 2;
            float point = data.Nodes![data.Motions![data.BlendMotions![middle]].Node].Coordinate;
            if (point <= coordinate)
                low = middle + 1;
            else
                high = middle - 1;
        }
        int lowerIndex = Math.Clamp(high, first, last);
        int upperIndex = Math.Clamp(low, first, last);
        int lower = data.BlendMotions![lowerIndex], upper = data.BlendMotions[upperIndex];
        float lowerCoordinate = data.Nodes![data.Motions![lower].Node].Coordinate;
        float upperCoordinate = data.Nodes[data.Motions[upper].Node].Coordinate;
        float blend = lower == upper ? 0 : (coordinate - lowerCoordinate) / (upperCoordinate - lowerCoordinate);
        for (int index = group.MotionFirst; index < group.MotionFirst + group.MotionCount; index++)
        {
            int m = data.GroupMotions![index];
            ref GraphMotion motion = ref motions[m];
            if (!paused)
            {
                motion.BlendWeight = m == lower ? 1 - blend : m == upper ? blend : 0;
                motion.Starts = 1;
                motion.Time += delta;
            }
            motion.Weight = motion.BlendWeight * groupWeight;
            motion.Running = false;
        }
    }
}

internal static class GraphTimes
{
    public static int LastStage(ReadOnlySpan<TimelineStage> stages, int stage, float time, bool paused) =>
        paused ? stage : Project(stages, stage, time).Stage;

    public static (int Stage, float Time) Project(ReadOnlySpan<TimelineStage> stages, int stage, float time)
    {
        while (stage < stages.Length && stages[stage].End == StageEnd.Duration && time >= stages[stage].Duration)
        {
            if (stage + 1 == stages.Length || stages[stage + 1].End == StageEnd.Settled)
                break;
            time -= stages[stage++].Duration;
        }
        return (stage, time);
    }
}
