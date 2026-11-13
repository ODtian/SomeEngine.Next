using SomeEngine.ECS.Entities;
using SomeEngine.ECS;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Animation.Pose;

/// <summary>Completes a level's concrete masked TRS jobs before publishing their pose results.</summary>
public struct PoseLayerSystem : ISystem<PoseNodeContext>
{
    private Query _layers;
    private List<JobHandle> _jobs;

    public void OnCreate(ref PoseNodeContext context)
    {
        _jobs = [];
        _layers = context.Frame.World.Query(AnimationGraph.WithTimeAccess(new QueryDescription()
            .WithStored<Timeline>().With<Timeline>().WithStored<Buffer<PoseLayerInstance>>()
            .Optional<AnimationLayers>()
            .Optional<Buffer<LocalPose>>().Optional<Buffer<PoseResult>>()));
    }

    public void OnUpdate(ref PoseNodeContext context)
    {
        _jobs.Clear();
        var frame = new LayerFrame { World = context.Frame.World, Level = context.Level, Jobs = _jobs };
        {
            using QueryScope read = _layers.Open();
            Visit(in read, ref frame);

            static void Visit(scoped in QueryScope rows, ref LayerFrame frame)
            {
                foreach (QueryChunk chunk in rows)
                {
                    var buffers0 = chunk.Has<Buffer<PoseLayerInstance>>() ? chunk.GetBuffers<PoseLayerInstance>() : default;
                    for (int rowIndex = 0; rowIndex < chunk.Count; rowIndex++)
                    {
                        foreach (ref PoseLayerInstance layer in buffers0[rowIndex].AsSpan())
                            if (layer.Node.Level == frame.Level) Gather(frame.World, chunk.Entities[rowIndex], ref layer, frame.Jobs);
                    }
                }

            }
        }
        foreach (JobHandle job in _jobs) job.Complete();
        {
            using QueryScope read = _layers.Open();
            Visit(in read, ref frame);

            static void Visit(scoped in QueryScope rows, ref LayerFrame frame)
            {
                World world = frame.World;
                foreach (QueryChunk chunk in rows)
                {
                    var buffers0 = chunk.Has<Buffer<PoseLayerInstance>>() ? chunk.GetBuffers<PoseLayerInstance>() : default;
                    for (int rowIndex = 0; rowIndex < chunk.Count; rowIndex++)
                    {
                        foreach (ref PoseLayerInstance layer in buffers0[rowIndex].AsSpan())
                        {
                            if (!layer.Active || layer.Node.Level != frame.Level) continue;
                            if (layer.Sampled)
                            {
                                var copy = (Values: layer.Samples.Output, Range: layer.Output.Range);
                                {
                                    using var poses = world.GetBuffer<LocalPose>(layer.Output.Entity);
                                    Visit(poses, ref copy);

                                    static void Visit(DynamicBuffer<LocalPose> poses, ref (BoneTransform[] Values, PoseRange Range) copy)
                                    {
                                        copy.Values.AsSpan()
                                                                        .CopyTo(LocalPose.Transforms(poses.AsSpan().Slice(copy.Range.Start, copy.Range.Count)));

                                    }
                                }
                            }
                            var output = (Index: layer.Output.Index, Basis: layer.Basis, Weight: layer.Weight, Sampled: layer.Sampled);
                            {
                                using var results = world.GetBuffer<PoseResult>(layer.Output.Entity);
                                Visit(results, ref output);

                                static void Visit(DynamicBuffer<PoseResult> results, ref (int Index, PoseResult Basis, float Weight, bool Sampled) output)
                                {
                                    ref PoseResult result = ref results[output.Index];
                                    result.Weight = output.Weight;
                                    if (!output.Sampled) return;
                                    result.Time = output.Basis.Time;
                                    result.Phase = output.Basis.Phase;
                                    result.Starts = output.Basis.Starts;
                                    result.RootMotion = new(output.Basis.RootMotion.Transform * result.RootMotion.Transform);
                                    result.Version++;

                                }
                            }
                            AnimationGraph.Submit(world, chunk.Entities[rowIndex], in layer.Placement, layer.Sampled);
                        }
                    }
                }

            }
        }
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
            AnimationLayers layers = world.ReadValue<AnimationLayers>(layer.Output.Entity);
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
        {
            using var poses = world.ReadBuffer<LocalPose>(reference.Entity);
            Visit(poses, ref copy);

            static void Visit(ReadOnlyBuffer<LocalPose> poses, ref (BoneTransform[] Values, PoseRange Range) copy)
            {
                LocalPose.Transforms(poses.AsSpan().Slice(copy.Range.Start, copy.Range.Count))
                                .CopyTo(copy.Values);

            }
        }
    }

    public void OnDestroy(ref PoseNodeContext context) { }

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
