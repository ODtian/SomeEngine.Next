using SomeEngine.ECS.Entities;
using SomeEngine.ECS;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Animation.Pose;

/// <summary>Fits weighted effector headings in each instance's workspace before ordinary pose composition and FK.</summary>
public struct EwbikSystem : ISystem<PoseNodeContext>
{
    private Query _bodies;
    private List<JobHandle> _jobs;

    public void OnCreate(ref PoseNodeContext context)
    {
        _jobs = [];
        _bodies = context.Frame.World.Query(AnimationGraph.WithTimeAccess(new QueryDescription()
            .WithStored<Timeline>().With<Timeline>().WithStored<Buffer<EwbikInstance>>()
            .Optional<AnimationLayers>()
            .Optional<Buffer<LocalPose>>().Optional<Buffer<PoseResult>>()));
    }

    public void OnUpdate(ref PoseNodeContext context)
    {
        _jobs.Clear();
        var frame = new EwbikFrame { World = context.Frame.World, Level = context.Level, Jobs = _jobs };
        {
            using QueryScope read = _bodies.Open();
            Visit(in read, ref frame);

            static void Visit(scoped in QueryScope rows, ref EwbikFrame frame)
            {
                foreach (QueryChunk chunk in rows)
                {
                    var buffers0 = chunk.Has<Buffer<EwbikInstance>>() ? chunk.GetBuffers<EwbikInstance>() : default;
                    for (int rowIndex = 0; rowIndex < chunk.Count; rowIndex++)
                    {
                        foreach (ref EwbikInstance body in buffers0[rowIndex].AsSpan())
                            if (body.Node.Level == frame.Level) Gather(frame.World, chunk.Entities[rowIndex], ref body, frame.Jobs);
                    }
                }

            }
        }
        foreach (JobHandle job in _jobs) job.Complete();
        {
            using QueryScope read = _bodies.Open();
            Visit(in read, ref frame);

            static void Visit(scoped in QueryScope rows, ref EwbikFrame frame)
            {
                World world = frame.World;
                foreach (QueryChunk chunk in rows)
                {
                    var buffers0 = chunk.Has<Buffer<EwbikInstance>>() ? chunk.GetBuffers<EwbikInstance>() : default;
                    for (int rowIndex = 0; rowIndex < chunk.Count; rowIndex++)
                    {
                        foreach (ref EwbikInstance body in buffers0[rowIndex].AsSpan())
                        {
                            if (!body.Active || body.Node.Level != frame.Level) continue;
                            if (body.Sampled)
                            {
                                var copy = (Values: body.Samples.Transforms, Range: body.Output.Range);
                                {
                                    using var poses = world.GetBuffer<LocalPose>(body.Output.Entity);
                                    Visit(poses, ref copy);

                                    static void Visit(DynamicBuffer<LocalPose> poses, ref (BoneTransform[] Values, PoseRange Range) copy)
                                    {
                                        copy.Values.AsSpan()
                                                                        .CopyTo(LocalPose.Transforms(poses.AsSpan().Slice(copy.Range.Start, copy.Range.Count)));

                                    }
                                }
                            }
                            var output = (Index: body.Output.Index, Basis: body.Basis, Weight: body.Weight, Sampled: body.Sampled);
                            {
                                using var results = world.GetBuffer<PoseResult>(body.Output.Entity);
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
                            AnimationGraph.Submit(world, chunk.Entities[rowIndex], in body.Placement, body.Sampled);
                        }
                    }
                }

            }
        }
    }

    private static void Gather(World world, Entity animation, ref EwbikInstance body, List<JobHandle> jobs)
    {
        AnimationLeafTime time = AnimationGraph.ReadTime(world, animation, in body.Placement);
        body.Active = time.Active;
        body.Sampled = false;
        if (!time.Active) return;
        body.Basis = PoseAnimation.ReadResult(world, in body.Source);
        body.Weight = body.Basis.Weight > 0 ? time.Weight : 0;
        if (world.Has<AnimationLayers>(body.Output.Entity))
        {
            AnimationLayers layers = world.ReadComponentData<AnimationLayers>(body.Output.Entity);
            if (body.Placement.Layer < layers.Count) body.Weight *= layers.Items[body.Placement.Layer].Weight;
        }
        if (body.Weight == 0) return;
        var copy = (Values: body.Samples.Transforms, Range: body.Source.Range);
        {
            using var poses = world.ReadBuffer<LocalPose>(body.Source.Entity);
            Visit(poses, ref copy);

            static void Visit(ReadOnlyBuffer<LocalPose> poses, ref (BoneTransform[] Values, PoseRange Range) copy)
            {
                LocalPose.Transforms(poses.AsSpan().Slice(copy.Range.Start, copy.Range.Count))
                                .CopyTo(copy.Values);

            }
        }
        body.Targets.CopyTo(body.Samples.Targets, 0);
        body.PreferenceWeights.CopyTo(body.Samples.PreferenceWeights, 0);
        body.Samples.Settings = body.Settings;
        body.Samples.ReadParents();
        body.Sampled = true;
        jobs.Add(PoseAnimation.GetScheduler(world).Schedule(new SolveBody(body.Samples), body.Samples.Accesses));
    }

    public void OnDestroy(ref PoseNodeContext context) { }

    private struct EwbikFrame
    {
        internal World World;
        internal int Level;
        internal List<JobHandle> Jobs;
    }

    private readonly record struct SolveBody(EwbikSamples Samples) : IJob
    {
        public void Execute() => Samples.Solve();
    }
}
