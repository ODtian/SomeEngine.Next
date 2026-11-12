using System.Numerics;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Animation.Pose;

/// <summary>Copies the selected pose and publishes movement from its bound corrected trajectory.</summary>
public struct MotionWarpSystem : ISystem<ImmediateSystemContext>, ISystem<PoseNodeContext>
{
    private Query _warps;

    public void OnCreate(ref ImmediateSystemContext context) =>
        _warps = context.World.Query(AnimationGraph.WithTimeAccess(new QueryDescription()
            .WithStored<Timeline>().With<Timeline>().WithStored<Buffer<MotionWarpInstance>>()
            .Optional<Buffer<LocalPose>>().Optional<Buffer<PoseResult>>()
            .Optional<AnimationLayers>()));

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
        {
            using QueryScope read = _warps.Open();
            Visit(in read, ref frame);

            static void Visit(scoped in QueryScope rows, ref WarpFrame frame)
            {
                foreach (QueryChunk chunk in rows)
                {
                    var buffers0 = chunk.Has<Buffer<MotionWarpInstance>>() ? chunk.GetBuffers<MotionWarpInstance>() : default;
                    for (int rowIndex = 0; rowIndex < chunk.Count; rowIndex++)
                    {
                        foreach (ref MotionWarpInstance instance in buffers0[rowIndex].AsSpan())
                        {
                            if (instance.Node.Level > frame.Level) frame.NextLevel = Math.Min(frame.NextLevel, instance.Node.Level);
                            if (instance.Node.Level == frame.Level) Evaluate(frame.World, chunk.Entities[rowIndex], ref instance);
                        }
                    }
                }

            }
        }
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
            AnimationLayers layers = world.ReadComponentData<AnimationLayers>(instance.Output.Entity);
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
            {
                using var poses = world.GetBuffer<LocalPose>(instance.Output.Entity);
                Visit(poses, ref copy);

                static void Visit(DynamicBuffer<LocalPose> poses, ref (PoseRange Source, PoseRange Output) copy)
                {
                    Span<BoneTransform> transforms = LocalPose.Transforms(poses.AsSpan());
                    transforms.Slice(copy.Source.Start, copy.Source.Count)
                        .CopyTo(transforms.Slice(copy.Output.Start, copy.Output.Count));

                }
            }
        }
        var output = (Index: instance.Output.Index, Source: source, Weight: weight, Motion: motion, Sampled: sampled);
        {
            using var results = world.GetBuffer<PoseResult>(instance.Output.Entity);
            Visit(results, ref output);

            static void Visit(DynamicBuffer<PoseResult> results, ref (int Index, PoseResult Source, float Weight, RootMotionDelta Motion, bool Sampled) output)
            {
                ref PoseResult result = ref results[output.Index];
                result.Weight = output.Weight;
                if (!output.Sampled) return;
                result.Time = output.Source.Time;
                result.Phase = output.Source.Phase;
                result.Starts = output.Source.Starts;
                result.RootMotion = new(output.Motion.Transform * result.RootMotion.Transform);
                result.Version++;

            }
        }
        instance.Finished |= time.Time.Time >= instance.Definition.Parameters[instance.Index].Warp.Source.Duration;
        AnimationGraph.Submit(world, animation, in instance.Placement, !instance.Finished);
    }

    public void OnDestroy(ref ImmediateSystemContext context) { }

    public void OnDestroy(ref PoseNodeContext context) { }

    private struct WarpFrame
    {
        internal World World;
        internal int Level, NextLevel;
    }
}
