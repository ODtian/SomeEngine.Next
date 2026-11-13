using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Animation.Pose;

public readonly record struct GpuStateParameters(GpuPoseStateMachine Machine, float Duration = float.PositiveInfinity);

public struct GpuStateInstance : IBufferElement
{
    public AnimationLeafBatch<GpuStateParameters> Definition;
    public int Index;
    public AnimationLeafPlacement Placement;
    public Entity Target;
    public float[] Controls;
    internal uint Starts;
    internal bool Finished;
}

/// <summary>Supplies the common leaf clock and controls while GPU storage holds the state-machine playback.</summary>
public readonly struct GpuStateLeaf : IAnimationLeaf<GpuStateParameters>
{
    public static MotionBoundary Describe(in GpuStateParameters parameters) => float.IsPositiveInfinity(parameters.Duration)
        ? new(StageEnd.Settled) : new(StageEnd.Duration, parameters.Duration);

    public static void Install(World world, Entity animation, Entity target,
        AnimationLeafBatch<GpuStateParameters> definitions, int index, in AnimationLeafPlacement placement)
    {
        if (!world.Has<Buffer<GpuStateInstance>>(animation)) world.AddBuffer<GpuStateInstance>(animation);
        GpuStateParameters parameters = definitions.Parameters[index];
        var instance = new GpuStateInstance
        {
            Definition = definitions,
            Index = index,
            Placement = placement,
            Target = target,
            Controls = new float[parameters.Machine.InputCount],
        };
        {
            using var instances = world.GetBuffer<GpuStateInstance>(animation);
            Visit(instances, ref instance);

            static void Visit(DynamicBuffer<GpuStateInstance> instances, ref GpuStateInstance instance)
            {
                if (instances.Count > 0 && instances[0].Placement.Starts != instance.Placement.Starts) instances.Clear();
                instances.Add(instance);

            }
        }
        GpuPoseTarget output = world.ReadValue<GpuPoseTarget>(target);
        output.Clip = null;
        output.StateMachine = parameters.Machine;
        output.Blend = null;
        output.Rig = null;
        output.Controls = instance.Controls;
        output.PlaybackTime = 0;
        output.Playing = false;
        output.Starts = placement.Starts;
        output.AnimationStarts = placement.Starts;
        output.ExtractRootMotion = false;
        world.Set(target, output);
    }

    public static void Release(World world, Entity animation)
    {
        using var instances = world.GetBuffer<GpuStateInstance>(animation);
        instances.Clear();

    }
}

internal struct GpuStateSystem : ISystem<ImmediateSystemContext>
{
    private Query _states;

    public void OnCreate(ref ImmediateSystemContext context) =>
        _states = context.World.Query(AnimationGraph.WithTimeAccess(new QueryDescription()
            .WithStored<Timeline>().With<Timeline>().WithStored<Buffer<GpuStateInstance>>()
            .Optional<GpuPoseTarget>()));

    public void OnUpdate(ref ImmediateSystemContext context)
    {
        using QueryScope rows = _states.Open();
        var targets = context.World.OpenWrite<GpuPoseTarget>();
        try
        {
            UpdateTargets(ref targets, in rows);
        }
        finally { targets.Dispose(); }
    }

    private static void UpdateTargets(ref WriteLookup<GpuPoseTarget> targets, scoped in QueryScope rows)
    {
        foreach (QueryChunk chunk in rows)
        {
            var timelines = chunk.Write<Timeline>();
            var states = chunk.GetBuffers<GpuStateInstance>();
            var times = AnimationGraph.ReadTimeColumn(chunk);
            var inlineTargets = chunk.Has<GpuPoseTarget>() ? chunk.Write<GpuPoseTarget>() : default;
            for (int row = 0; row < chunk.Count; row++)
            {
                Entity animation = chunk.Entities[row];
                ref Timeline timeline = ref timelines[row];
                foreach (ref GpuStateInstance instance in states[row].AsSpan())
                {
                    AnimationLeafTime time = times.Read(row, in timeline, in instance.Placement);
                    if (instance.Starts != time.Starts) { instance.Starts = time.Starts; instance.Finished = false; }
                    if (!time.Active || instance.Finished) continue;
                    GpuStateParameters parameters = instance.Definition.Parameters[instance.Index];
                    float elapsed = Math.Min(time.Time.Time, parameters.Duration);
                    ref GpuPoseTarget output = ref (instance.Target == animation
                        ? ref inlineTargets[row] : ref targets.Get(instance.Target));
                    output.Clip = null;
                    output.StateMachine = parameters.Machine;
                    output.Blend = null;
                    output.Rig = null;
                    output.Controls = instance.Controls;
                    output.PlaybackTime = elapsed;
                    output.Playing = true;
                    output.Starts = time.Starts;
                    output.ExtractRootMotion = false;
                    instance.Finished = elapsed >= parameters.Duration;
                    times.Submit(row, ref timeline, in instance.Placement, !instance.Finished);
                }
            }
        }
    }

    public void OnDestroy(ref ImmediateSystemContext context) { }
}
