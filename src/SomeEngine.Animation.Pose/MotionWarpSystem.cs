using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Animation.Pose;

/// <summary>Copies the selected pose and publishes movement from its bound corrected trajectory.</summary>
public struct MotionWarpSystem : ISystem<ImmediateSystemContext>, ISystem<PoseNodeContext>
{
    private QueryHandle _warps;

    public void OnCreate(ref ImmediateSystemContext context) =>
        _warps = context.World.Query(AnimationGraph.WithTimeAccess(context.World.QueryDefinition()
            .ReadWrite<Timeline>().Enabled<Timeline>().WriteBuffer<MotionWarpInstance>()
            .OptionalBuffer<LocalPose>(QueryAccess.ReadWrite).OptionalBuffer<PoseResult>(QueryAccess.ReadWrite)
            .Optional<AnimationLayers>(QueryAccess.Read)));

    public void OnUpdate(ref ImmediateSystemContext context)
    {
        var frame = new WarpFrame { World = context.World };
        do
        {
            Evaluate(ref frame);
            frame.Level = frame.NextLevel;
        } while (frame.Level != int.MaxValue);
    }

    public void OnCreate(ref PoseNodeContext context)
    {
        ImmediateSystemContext frame = context.Frame;
        OnCreate(ref frame);
    }

    public void OnUpdate(ref PoseNodeContext context)
    {
        var frame = new WarpFrame { World = context.Frame.World, Level = context.Level };
        Evaluate(ref frame);
    }

    private void Evaluate(ref WarpFrame frame)
    {
        frame.NextLevel = int.MaxValue;
        frame.World.ExecuteQuery(_warps, ref frame, static (QueryCursor rows, ref WarpFrame frame) =>
        {
            foreach (QueryRow row in rows.Rows)
                foreach (ref MotionWarpInstance instance in row.Buffer<MotionWarpInstance>().AsSpan())
                {
                    if (instance.Node.Level > frame.Level) frame.NextLevel = Math.Min(frame.NextLevel, instance.Node.Level);
                    if (instance.Node.Level == frame.Level) Evaluate(frame.World, row.Entity, ref instance);
                }
        });
    }

    private static void Evaluate(World world, Entity animation, ref MotionWarpInstance instance)
    {
        AnimationLeafTime time = AnimationGraph.ReadTime(world, animation, in instance.Placement);
        if (instance.Starts != time.Starts)
        {
            instance.Starts = time.Starts;
            instance.Finished = false;
            instance.PreviousMotion = Matrix4x4.Identity;
            instance.SourceVersion = 0;
        }
        if (!time.Active) return;
        PoseResult source = PoseAnimation.ReadResult(world, in instance.Source);
        if (instance.SourceStarts != source.Starts)
        {
            instance.SourceStarts = source.Starts;
            instance.PreviousMotion = Matrix4x4.Identity;
            instance.SourceVersion = 0;
        }
        float weight = source.Weight == 0 ? 0 : time.Weight;
        if (world.Has<AnimationLayers>(instance.Output.Entity))
        {
            AnimationLayers layers = world.Read<AnimationLayers>(instance.Output.Entity);
            if (instance.Placement.Layer < layers.Count) weight *= layers.Items[instance.Placement.Layer].Weight;
        }
        bool sampled = !instance.Finished && instance.SourceVersion != source.Version;
        RootMotionDelta motion = RootMotionDelta.Identity;
        if (sampled)
        {
            Matrix4x4 current = instance.Input.Sample((float)source.Time);
            Matrix4x4.Invert(instance.PreviousMotion, out Matrix4x4 inversePrevious);
            motion = new(current * inversePrevious);
            instance.PreviousMotion = current;
            instance.SourceVersion = source.Version;
            var copy = (Source: instance.Source.Range, Output: instance.Output.Range);
            world.ExecuteBufferWrite<LocalPose, (PoseRange Source, PoseRange Output)>(instance.Output.Entity, ref copy,
                static (poses, ref copy) =>
                {
                    Span<BoneTransform> transforms = LocalPose.Transforms(poses.AsSpan());
                    transforms.Slice(copy.Source.Start, copy.Source.Count)
                        .CopyTo(transforms.Slice(copy.Output.Start, copy.Output.Count));
                });
        }
        var output = (Index: instance.Output.Index, Source: source, Weight: weight, Motion: motion, Sampled: sampled);
        world.ExecuteBufferWrite<PoseResult, (int Index, PoseResult Source, float Weight, RootMotionDelta Motion, bool Sampled)>(
            instance.Output.Entity, ref output, static (results, ref output) =>
            {
                ref PoseResult result = ref results[output.Index];
                result.Weight = output.Weight;
                if (!output.Sampled) return;
                result.Time = output.Source.Time;
                result.Phase = output.Source.Phase;
                result.Starts = output.Source.Starts;
                result.RootMotion = new(output.Motion.Transform * result.RootMotion.Transform);
                result.Version++;
            });
        instance.Finished |= time.Time.Time >= instance.Definition.Parameters[instance.Index].Warp.Source.Duration;
        AnimationGraph.Submit(world, animation, in instance.Placement, !instance.Finished);
    }

    public void OnDestroy(ref ImmediateSystemContext context) => context.World.ReleaseQuery(_warps);

    public void OnDestroy(ref PoseNodeContext context) => context.Frame.World.ReleaseQuery(_warps);

    private struct WarpFrame
    {
        internal World World;
        internal int Level, NextLevel;
    }
}
