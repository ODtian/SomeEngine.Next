using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Animation.Pose;

/// <summary>Completes a level's concrete masked TRS jobs before publishing their pose results.</summary>
public struct PoseLayerSystem : ISystem<PoseNodeContext>
{
    private QueryHandle _layers;
    private List<JobHandle> _jobs;

    public void OnCreate(ref PoseNodeContext context)
    {
        _jobs = [];
        _layers = context.Frame.World.Query(AnimationGraph.WithTimeAccess(context.Frame.World.QueryDefinition()
            .ReadWrite<Timeline>().Enabled<Timeline>().WriteBuffer<PoseLayerInstance>()
            .Optional<AnimationLayers>(QueryAccess.Read)
            .OptionalBuffer<LocalPose>(QueryAccess.ReadWrite).OptionalBuffer<PoseResult>(QueryAccess.ReadWrite)));
    }

    public void OnUpdate(ref PoseNodeContext context)
    {
        _jobs.Clear();
        var frame = new LayerFrame { World = context.Frame.World, Level = context.Level, Jobs = _jobs };
        frame.World.ExecuteQuery(_layers, ref frame, static (QueryCursor rows, ref LayerFrame frame) =>
        {
            foreach (QueryRow row in rows.Rows)
                foreach (ref PoseLayerInstance layer in row.Buffer<PoseLayerInstance>().AsSpan())
                    if (layer.Node.Level == frame.Level) Gather(frame.World, row.Entity, ref layer, frame.Jobs);
        });
        foreach (JobHandle job in _jobs) job.Complete();
        frame.World.ExecuteQuery(_layers, ref frame, static (QueryCursor rows, ref LayerFrame frame) =>
        {
            World world = frame.World;
            foreach (QueryRow row in rows.Rows)
                foreach (ref PoseLayerInstance layer in row.Buffer<PoseLayerInstance>().AsSpan())
                {
                    if (!layer.Active || layer.Node.Level != frame.Level) continue;
                    if (layer.Sampled)
                    {
                        var copy = (Values: layer.Samples.Output, Range: layer.Output.Range);
                        world.ExecuteBufferWrite<LocalPose, (BoneTransform[] Values, PoseRange Range)>(layer.Output.Entity,
                            ref copy, static (poses, ref copy) => copy.Values.AsSpan()
                                .CopyTo(LocalPose.Transforms(poses.AsSpan().Slice(copy.Range.Start, copy.Range.Count))));
                    }
                    var output = (Index: layer.Output.Index, Basis: layer.Basis, Weight: layer.Weight, Sampled: layer.Sampled);
                    world.ExecuteBufferWrite<PoseResult, (int Index, PoseResult Basis, float Weight, bool Sampled)>(layer.Output.Entity,
                        ref output, static (results, ref output) =>
                        {
                            ref PoseResult result = ref results[output.Index];
                            result.Weight = output.Weight;
                            if (!output.Sampled) return;
                            result.Time = output.Basis.Time;
                            result.Phase = output.Basis.Phase;
                            result.Starts = output.Basis.Starts;
                            result.RootMotion = new(output.Basis.RootMotion.Transform * result.RootMotion.Transform);
                            result.Version++;
                        });
                    AnimationGraph.Submit(world, row.Entity, in layer.Placement, layer.Sampled);
                }
        });
    }

    private static void Gather(World world, Entity animation, ref PoseLayerInstance layer, List<JobHandle> jobs)
    {
        AnimationLeafTime time = AnimationGraph.ReadTime(world, animation, in layer.Placement);
        layer.Active = time.Active;
        layer.Sampled = false;
        if (!time.Active) return;
        layer.Basis = PoseAnimation.ReadResult(world, in layer.Base);
        layer.Weight = layer.Basis.Weight > 0 ? time.Weight : 0;
        if (layer.Weight == 0) return;
        PoseResult source = PoseAnimation.ReadResult(world, in layer.Source);
        var operation = new AnimationLayer { Mode = LayerMode.Override, Weight = 1 };
        if (world.Has<AnimationLayers>(layer.Output.Entity))
        {
            AnimationLayers layers = world.Read<AnimationLayers>(layer.Output.Entity);
            if (layer.Placement.Layer < layers.Count) operation = layers.Items[layer.Placement.Layer];
        }
        Copy(world, in layer.Base, layer.Samples.Base);
        Copy(world, in layer.Source, layer.Samples.Source);
        layer.Sampled = true;
        jobs.Add(PoseAnimation.GetScheduler(world).Schedule(new LayerBones(layer.Samples, operation.Mode, operation.Weight * source.Weight),
            layer.Samples.Accesses));
    }

    private static void Copy(World world, in PoseResultReference reference, BoneTransform[] values)
    {
        var copy = (Values: values, Range: reference.Range);
        world.ExecuteBufferRead<LocalPose, (BoneTransform[] Values, PoseRange Range)>(reference.Entity, ref copy,
            static (poses, ref copy) => LocalPose.Transforms(poses.AsSpan().Slice(copy.Range.Start, copy.Range.Count))
                .CopyTo(copy.Values));
    }

    public void OnDestroy(ref PoseNodeContext context) => context.Frame.World.ReleaseQuery(_layers);

    private struct LayerFrame
    {
        internal World World;
        internal int Level;
        internal List<JobHandle> Jobs;
    }

    private readonly record struct LayerBones(PoseLayerSamples Samples, LayerMode Mode, float Weight) : IJob
    {
        public void Execute()
        {
            ReadOnlySpan<BoneTransform> basis = Samples.Base, source = Samples.Source;
            ReadOnlySpan<float> mask = Samples.Mask.Weights;
            Span<BoneTransform> output = Samples.Output;
            if (Mode == LayerMode.Override)
            {
                for (int bone = 0; bone < output.Length; bone++)
                    output[bone] = BoneTransformOps.Lerp(in basis[bone], in source[bone], Weight * mask[bone]);
                return;
            }
            ReadOnlySpan<BoneTransform> reference = Samples.Skeleton.ReferencePose;
            for (int bone = 0; bone < output.Length; bone++)
                output[bone] = BoneTransformOps.Additive(in basis[bone], in source[bone], in reference[bone], Weight * mask[bone]);
        }
    }
}
