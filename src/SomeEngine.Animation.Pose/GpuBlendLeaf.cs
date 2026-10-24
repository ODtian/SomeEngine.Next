using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Animation.Pose;

public readonly record struct GpuBlendParameters(GpuPoseBlend Blend, float Duration = float.PositiveInfinity);

[BufferCapacity(1)]
public struct GpuBlendInstance : IBufferElement
{
    public AnimationLeafBatch<GpuBlendParameters> Definition;
    public int Index;
    public AnimationLeafPlacement Placement;
    public Entity Target;
    internal float[] Controls;
    internal uint Starts;
    internal bool Finished;
}

/// <summary>Passes the common graph clock and instance parameters to a GPU local-pose composition.</summary>
public readonly struct GpuBlendLeaf : IAnimationLeaf<GpuBlendParameters>
{
    public static MotionBoundary Describe(in GpuBlendParameters parameters) => float.IsPositiveInfinity(parameters.Duration)
        ? new(StageEnd.Settled) : new(StageEnd.Duration, parameters.Duration);

    public static void Install(World world, Entity animation, Entity target,
        AnimationLeafBatch<GpuBlendParameters> definitions, int index, in AnimationLeafPlacement placement)
    {
        if (!world.HasBuffer<GpuBlendInstance>(animation)) world.AddBuffer<GpuBlendInstance>(animation);
        if (!world.Has<GraphParameters>(animation)) world.Add(animation, new GraphParameters());
        GpuPoseBlend blend = definitions.Parameters[index].Blend;
        var instance = new GpuBlendInstance
        {
            Definition = definitions, Index = index, Placement = placement, Target = target,
            Controls = new float[blend.InputCount],
        };
        world.ExecuteBufferWrite<GpuBlendInstance, GpuBlendInstance>(animation, ref instance,
            static (instances, ref instance) =>
            {
                if (instances.Count > 0 && instances.Read(0).Placement.Starts != instance.Placement.Starts) instances.Clear();
                instances.Add(instance);
            });
        GpuPoseTarget output = world.Read<GpuPoseTarget>(target);
        output.Clip = null;
        output.StateMachine = null;
        output.Blend = blend;
        output.Rig = null;
        output.Controls = instance.Controls;
        output.PlaybackTime = 0;
        output.Playing = false;
        output.Starts = placement.Starts;
        world.Replace(target, output);
    }

    public static void Release(World world, Entity animation) =>
        world.ExecuteBufferWrite<GpuBlendInstance>(animation, static instances => instances.Clear());
}

internal struct GpuBlendSystem : ISystem<ImmediateSystemContext>
{
    private QueryHandle _blends;
    public void OnCreate(ref ImmediateSystemContext context) =>
        _blends = context.World.Query(AnimationGraph.WithTimeAccess(context.World.QueryDefinition()
            .ReadWrite<Timeline>().Enabled<Timeline>().WriteBuffer<GpuBlendInstance>().Read<GraphParameters>()
            .Optional<GpuPoseTarget>(QueryAccess.ReadWrite)));

    public void OnUpdate(ref ImmediateSystemContext context)
    {
        World world = context.World;
        world.ExecuteQuery(_blends, ref world, static (QueryCursor rows, ref World world) =>
        {
            foreach (QueryRow row in rows.Rows)
            {
                GraphParameters controls = row.Read<GraphParameters>();
                foreach (ref GpuBlendInstance instance in row.Buffer<GpuBlendInstance>().AsSpan())
                {
                    AnimationLeafTime time = AnimationGraph.ReadTime(world, row.Entity, in instance.Placement);
                    if (instance.Starts != time.Starts) { instance.Starts = time.Starts; instance.Finished = false; }
                    if (!time.Active || instance.Finished) continue;
                    GpuBlendParameters parameters = instance.Definition.Parameters[instance.Index];
                    float elapsed = Math.Min(time.Time.Time, parameters.Duration);
                    for (int input = 0; input < instance.Controls.Length; input++) instance.Controls[input] = controls.Values[input];
                    GpuPoseTarget output = world.Read<GpuPoseTarget>(instance.Target);
                    output.Clip = null;
                    output.StateMachine = null;
                    output.Blend = parameters.Blend;
                    output.Rig = null;
                    output.Controls = instance.Controls;
                    output.PlaybackTime = elapsed;
                    output.Playing = true;
                    output.Starts = time.Starts;
                    world.Replace(instance.Target, output);
                    instance.Finished = elapsed >= parameters.Duration;
                    AnimationGraph.Submit(world, row.Entity, in instance.Placement, !instance.Finished);
                }
            }
        });
    }

    public void OnDestroy(ref ImmediateSystemContext context) => context.World.ReleaseQuery(_blends);
}
