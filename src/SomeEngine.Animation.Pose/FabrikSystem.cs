using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Animation.Pose;

/// <summary>Solves each active chain in its retained workspace, then publishes local poses for ordinary FK.</summary>
public struct FabrikSystem : ISystem<PoseNodeContext>
{
    private QueryHandle _chains;
    private List<JobHandle> _jobs;

    public void OnCreate(ref PoseNodeContext context)
    {
        _jobs = [];
        _chains = context.Frame.World.Query(AnimationGraph.WithTimeAccess(context.Frame.World.QueryDefinition()
            .ReadWrite<Timeline>().Enabled<Timeline>().WriteBuffer<FabrikInstance>()
            .Optional<AnimationLayers>(QueryAccess.Read)
            .OptionalBuffer<LocalPose>(QueryAccess.ReadWrite).OptionalBuffer<PoseResult>(QueryAccess.ReadWrite)));
    }

    public void OnUpdate(ref PoseNodeContext context)
    {
        _jobs.Clear();
        var frame = new FabrikFrame { World = context.Frame.World, Level = context.Level, Jobs = _jobs };
        frame.World.ExecuteQuery(_chains, ref frame, static (QueryCursor rows, ref FabrikFrame frame) =>
        {
            foreach (QueryRow row in rows.Rows)
                foreach (ref FabrikInstance chain in row.Buffer<FabrikInstance>().AsSpan())
                    if (chain.Node.Level == frame.Level) Gather(frame.World, row.Entity, ref chain, frame.Jobs);
        });
        foreach (JobHandle job in _jobs) job.Complete();
        frame.World.ExecuteQuery(_chains, ref frame, static (QueryCursor rows, ref FabrikFrame frame) =>
        {
            World world = frame.World;
            foreach (QueryRow row in rows.Rows)
                foreach (ref FabrikInstance chain in row.Buffer<FabrikInstance>().AsSpan())
                {
                    if (!chain.Active || chain.Node.Level != frame.Level) continue;
                    if (chain.Sampled)
                    {
                        var copy = (Values: chain.Samples.Transforms, Range: chain.Output.Range);
                        world.ExecuteBufferWrite<LocalPose, (BoneTransform[] Values, PoseRange Range)>(chain.Output.Entity,
                            ref copy, static (poses, ref copy) => copy.Values.AsSpan()
                                .CopyTo(LocalPose.Transforms(poses.AsSpan().Slice(copy.Range.Start, copy.Range.Count))));
                    }
                    var output = (Index: chain.Output.Index, Basis: chain.Basis, Weight: chain.Weight, Sampled: chain.Sampled);
                    world.ExecuteBufferWrite<PoseResult, (int Index, PoseResult Basis, float Weight, bool Sampled)>(chain.Output.Entity,
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
                    AnimationGraph.Submit(world, row.Entity, in chain.Placement, chain.Sampled);
                }
        });
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
            AnimationLayers layers = world.Read<AnimationLayers>(chain.Output.Entity);
            if (chain.Placement.Layer < layers.Count) chain.Weight *= layers.Items[chain.Placement.Layer].Weight;
        }
        if (chain.Weight == 0) return;
        var copy = (Values: chain.Samples.Transforms, Range: chain.Source.Range);
        world.ExecuteBufferRead<LocalPose, (BoneTransform[] Values, PoseRange Range)>(chain.Source.Entity,
            ref copy, static (poses, ref copy) => LocalPose.Transforms(poses.AsSpan().Slice(copy.Range.Start, copy.Range.Count))
                .CopyTo(copy.Values));
        chain.Samples.ReadParents();
        chain.Sampled = true;
        jobs.Add(JobSystem.Schedule(new SolveChain(chain.Samples, chain.Target), chain.Samples.Accesses));
    }

    public void OnDestroy(ref PoseNodeContext context) => context.Frame.World.ReleaseQuery(_chains);

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
