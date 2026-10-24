using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Animation.Pose;

public readonly record struct GpuStateParameters(GpuPoseStateMachine Machine, float Duration = float.PositiveInfinity);

[BufferCapacity(1)]
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
        if (!world.HasBuffer<GpuStateInstance>(animation)) world.AddBuffer<GpuStateInstance>(animation);
        GpuStateParameters parameters = definitions.Parameters[index];
        var instance = new GpuStateInstance
        {
            Definition = definitions, Index = index, Placement = placement, Target = target,
            Controls = new float[parameters.Machine.InputCount],
        };
        world.ExecuteBufferWrite<GpuStateInstance, GpuStateInstance>(animation, ref instance,
            static (instances, ref instance) =>
            {
                if (instances.Count > 0 && instances.Read(0).Placement.Starts != instance.Placement.Starts) instances.Clear();
                instances.Add(instance);
            });
        GpuPoseTarget output = world.Read<GpuPoseTarget>(target);
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
        world.Replace(target, output);
    }

    public static void Release(World world, Entity animation) =>
        world.ExecuteBufferWrite<GpuStateInstance>(animation, static instances => instances.Clear());
}

internal struct GpuStateSystem : ISystem<ImmediateSystemContext>
{
    private QueryHandle _states;

    public void OnCreate(ref ImmediateSystemContext context) =>
        _states = context.World.Query(AnimationGraph.WithTimeAccess(context.World.QueryDefinition()
            .ReadWrite<Timeline>().Enabled<Timeline>().WriteBuffer<GpuStateInstance>()
            .Optional<GpuPoseTarget>(QueryAccess.ReadWrite)));

    public unsafe void OnUpdate(ref ImmediateSystemContext context)
    {
        World world = context.World;
        world.ExecuteQuery(_states, ref world, static (scoped QueryCursor rows, ref World world) =>
            world.ExecuteComponentWrite<GpuPoseTarget, QueryCursor>(ref rows, &UpdateTargets));
    }

    private static void UpdateTargets(ComponentWriteBatch<GpuPoseTarget> targets, ref QueryCursor rows)
    {
        foreach (QueryRow row in rows.Rows)
        foreach (ref GpuStateInstance instance in row.Buffer<GpuStateInstance>().AsSpan())
        {
            AnimationLeafTime time = AnimationGraph.ReadTime(in row, in instance.Placement);
            if (instance.Starts != time.Starts) { instance.Starts = time.Starts; instance.Finished = false; }
            if (!time.Active || instance.Finished) continue;
            GpuStateParameters parameters = instance.Definition.Parameters[instance.Index];
            float elapsed = Math.Min(time.Time.Time, parameters.Duration);
            ref GpuPoseTarget output = ref targets.Write(instance.Target);
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
            AnimationGraph.Submit(in row, in instance.Placement, !instance.Finished);
        }
    }

    public void OnDestroy(ref ImmediateSystemContext context) => context.World.ReleaseQuery(_states);
}
