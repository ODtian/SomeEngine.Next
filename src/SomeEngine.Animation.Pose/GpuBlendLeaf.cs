using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Animation.Pose;

public readonly record struct GpuBlendParameters(GpuPoseBlend Blend, float Duration = float.PositiveInfinity);

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
        if (!world.Has<Buffer<GpuBlendInstance>>(animation)) world.AddBuffer<GpuBlendInstance>(animation);
        if (!world.Has<GraphParameters>(animation)) world.Add(animation, new GraphParameters());
        GpuPoseBlend blend = definitions.Parameters[index].Blend;
        var instance = new GpuBlendInstance
        {
            Definition = definitions,
            Index = index,
            Placement = placement,
            Target = target,
            Controls = new float[blend.InputCount],
        };
        {
            using var instances = world.GetBuffer<GpuBlendInstance>(animation);
            Visit(instances, ref instance);

            static void Visit(DynamicBuffer<GpuBlendInstance> instances, ref GpuBlendInstance instance)
            {
                if (instances.Count > 0 && instances[0].Placement.Starts != instance.Placement.Starts) instances.Clear();
                instances.Add(instance);

            }
        }
        GpuPoseTarget output = world.ReadValue<GpuPoseTarget>(target);
        output.Clip = null;
        output.StateMachine = null;
        output.Blend = blend;
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
        using var instances = world.GetBuffer<GpuBlendInstance>(animation);
        instances.Clear();

    }
}

internal struct GpuBlendSystem : ISystem<ImmediateSystemContext>
{
    private Query _blends;
    public void OnCreate(ref ImmediateSystemContext context) =>
        _blends = context.World.Query(AnimationGraph.WithTimeAccess(new QueryDescription()
            .WithStored<Timeline>().With<Timeline>().WithStored<Buffer<GpuBlendInstance>>().WithStored<GraphParameters>()
            .Optional<GpuPoseTarget>()));

    public void OnUpdate(ref ImmediateSystemContext context)
    {
        World world = context.World;
        {
            using QueryScope read = _blends.Open();
            Visit(in read, ref world);

            static void Visit(scoped in QueryScope rows, ref World world)
            {
                foreach (QueryChunk chunk in rows)
                {
                    var timelines = chunk.Write<Timeline>();
                    var blends = chunk.GetBuffers<GpuBlendInstance>();
                    var times = AnimationGraph.ReadTimeColumn(chunk);
                    for (int row = 0; row < chunk.Count; row++)
                    {
                        ref Timeline timeline = ref timelines[row];
                        GraphParameters controls = chunk.Read<GraphParameters>()[row];
                        foreach (ref GpuBlendInstance instance in blends[row].AsSpan())
                        {
                            AnimationLeafTime time = times.Read(row, in timeline, in instance.Placement);
                            if (instance.Starts != time.Starts) { instance.Starts = time.Starts; instance.Finished = false; }
                            if (!time.Active || instance.Finished) continue;
                            GpuBlendParameters parameters = instance.Definition.Parameters[instance.Index];
                            float elapsed = Math.Min(time.Time.Time, parameters.Duration);
                            for (int input = 0; input < instance.Controls.Length; input++) instance.Controls[input] = controls.Values[input];
                            GpuPoseTarget output = world.ReadValue<GpuPoseTarget>(instance.Target);
                            output.Clip = null;
                            output.StateMachine = null;
                            output.Blend = parameters.Blend;
                            output.Rig = null;
                            output.Controls = instance.Controls;
                            output.PlaybackTime = elapsed;
                            output.Playing = true;
                            output.Starts = time.Starts;
                            output.ExtractRootMotion = false;
                            world.Set(instance.Target, output);
                            instance.Finished = elapsed >= parameters.Duration;
                            times.Submit(row, ref timeline, in instance.Placement, !instance.Finished);
                        }
                    }
                }

            }
        }
    }

    public void OnDestroy(ref ImmediateSystemContext context) { }
}
