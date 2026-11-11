using System.Numerics;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Animation.Pose;

/// <summary>Solves each active chain in its retained workspace, then publishes local poses for ordinary FK.</summary>
public struct FabrikSystem : ISystem<PoseNodeContext>
{
    private Query _chains;
    private List<JobHandle> _jobs;

    public void OnCreate(ref PoseNodeContext context)
    {
        _jobs = [];
        _chains = context.Frame.World.Query(AnimationGraph.WithTimeAccess(new QueryDescription()
            .WithStored<Timeline>().With<Timeline>().WithStored<Buffer<FabrikInstance>>()
            .Optional<AnimationLayers>()
            .Optional<Buffer<LocalPose>>().Optional<Buffer<PoseResult>>()));
    }

    public void OnUpdate(ref PoseNodeContext context)
    {
        _jobs.Clear();
        var frame = new FabrikFrame { World = context.Frame.World, Level = context.Level, Jobs = _jobs };
        {
            using QueryScope read = _chains.Open();
            Visit(in read, ref frame);

            static void Visit(scoped in QueryScope rows, ref FabrikFrame frame)
            {
                foreach (QueryChunk chunk in rows)
                {
                    var buffers0 = chunk.Has<Buffer<FabrikInstance>>() ? chunk.GetBuffers<FabrikInstance>() : default;
                    for (int rowIndex = 0; rowIndex < chunk.Count; rowIndex++)
                    {
                        foreach (ref FabrikInstance chain in buffers0[rowIndex].AsSpan())
                            if (chain.Node.Level == frame.Level) Gather(frame.World, chunk.Entities[rowIndex], ref chain, frame.Jobs);
                    }
                }

            }
        }
        foreach (JobHandle job in _jobs) job.Complete();
        {
            using QueryScope read = _chains.Open();
            Visit(in read, ref frame);

            static void Visit(scoped in QueryScope rows, ref FabrikFrame frame)
            {
                World world = frame.World;
                foreach (QueryChunk chunk in rows)
                {
                    var buffers0 = chunk.Has<Buffer<FabrikInstance>>() ? chunk.GetBuffers<FabrikInstance>() : default;
                    for (int rowIndex = 0; rowIndex < chunk.Count; rowIndex++)
                    {
                        foreach (ref FabrikInstance chain in buffers0[rowIndex].AsSpan())
                        {
                            if (!chain.Active || chain.Node.Level != frame.Level) continue;
                            if (chain.Sampled)
                            {
                                var copy = (Values: chain.Samples.Transforms, Range: chain.Output.Range);
                                {
                                    using var poses = world.GetBuffer<LocalPose>(chain.Output.Entity);
                                    Visit(poses, ref copy);

                                    static void Visit(DynamicBuffer<LocalPose> poses, ref (BoneTransform[] Values, PoseRange Range) copy)
                                    {
                                        copy.Values.AsSpan()
                                                                        .CopyTo(LocalPose.Transforms(poses.AsSpan().Slice(copy.Range.Start, copy.Range.Count)));

                                    }
                                }
                            }
                            var output = (Index: chain.Output.Index, Basis: chain.Basis, Weight: chain.Weight, Sampled: chain.Sampled);
                            {
                                using var results = world.GetBuffer<PoseResult>(chain.Output.Entity);
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
                            AnimationGraph.Submit(world, chunk.Entities[rowIndex], in chain.Placement, chain.Sampled);
                        }
                    }
                }

            }
        }
    }

    private static void Gather(World world, Entity animation, ref FabrikInstance chain, List<JobHandle> jobs)
    {
        AnimationLeafTime time = AnimationGraph.ReadTime(world, animation, in chain.Placement);
        chain.Active = time.Active;
        chain.Sampled = false;
        if (!time.Active) return;
        chain.Basis = PoseAnimation.ReadResult(world, in chain.Source);
        chain.Weight = chain.Basis.Weight > 0 ? time.Weight : 0;
        if (world.Has<AnimationLayers>(chain.Output.Entity))
        {
            AnimationLayers layers = world.ReadComponentData<AnimationLayers>(chain.Output.Entity);
            if (chain.Placement.Layer < layers.Count) chain.Weight *= layers.Items[chain.Placement.Layer].Weight;
        }
        if (chain.Weight == 0) return;
        var copy = (Values: chain.Samples.Transforms, Range: chain.Source.Range);
        {
            using var poses = world.ReadBuffer<LocalPose>(chain.Source.Entity);
            Visit(poses, ref copy);

            static void Visit(ReadOnlyBuffer<LocalPose> poses, ref (BoneTransform[] Values, PoseRange Range) copy)
            {
                LocalPose.Transforms(poses.AsSpan().Slice(copy.Range.Start, copy.Range.Count))
                                .CopyTo(copy.Values);

            }
        }
        chain.Samples.ReadParents();
        chain.Sampled = true;
        jobs.Add(PoseAnimation.GetScheduler(world).Schedule(new SolveChain(chain.Samples, chain.Target), chain.Samples.Accesses));
    }

    public void OnDestroy(ref PoseNodeContext context) { }

    private struct FabrikFrame
    {
        internal World World;
        internal int Level;
        internal List<JobHandle> Jobs;
    }

    private readonly record struct SolveChain(FabrikSamples Samples, Vector3 Target) : IJob
    {
        public void Execute() => Samples.Solve(Target);
    }
}
