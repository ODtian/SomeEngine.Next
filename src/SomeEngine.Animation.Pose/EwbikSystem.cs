using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Animation.Pose;

/// <summary>Fits weighted effector headings in each instance's workspace before ordinary pose composition and FK.</summary>
public struct EwbikSystem : ISystem<PoseNodeContext>
{
    private QueryHandle _bodies;
    private List<JobHandle> _jobs;

    public void OnCreate(ref PoseNodeContext context)
    {
        _jobs = [];
        _bodies = context.Frame.World.Query(AnimationGraph.WithTimeAccess(context.Frame.World.QueryDefinition()
            .ReadWrite<Timeline>().Enabled<Timeline>().WriteBuffer<EwbikInstance>()
            .Optional<AnimationLayers>(QueryAccess.Read)
            .OptionalBuffer<LocalPose>(QueryAccess.ReadWrite).OptionalBuffer<PoseResult>(QueryAccess.ReadWrite)));
    }

    public void OnUpdate(ref PoseNodeContext context)
    {
        _jobs.Clear();
        var frame = new EwbikFrame { World = context.Frame.World, Level = context.Level, Jobs = _jobs };
        frame.World.ExecuteQuery(_bodies, ref frame, static (scoped QueryCursor rows, ref EwbikFrame frame) =>
        {
            foreach (QueryRow row in rows.Rows)
                foreach (ref EwbikInstance body in row.Buffer<EwbikInstance>().AsSpan())
                    if (body.Node.Level == frame.Level) Gather(frame.World, row.Entity, ref body, frame.Jobs);
        });
        foreach (JobHandle job in _jobs) job.Complete();
        frame.World.ExecuteQuery(_bodies, ref frame, static (scoped QueryCursor rows, ref EwbikFrame frame) =>
        {
            World world = frame.World;
            foreach (QueryRow row in rows.Rows)
                foreach (ref EwbikInstance body in row.Buffer<EwbikInstance>().AsSpan())
                {
                    if (!body.Active || body.Node.Level != frame.Level) continue;
                    if (body.Sampled)
                    {
                        var copy = (Values: body.Samples.Transforms, Range: body.Output.Range);
                        world.ExecuteBufferWrite<LocalPose, (BoneTransform[] Values, PoseRange Range)>(body.Output.Entity,
                            ref copy, static (poses, ref copy) => copy.Values.AsSpan()
                                .CopyTo(LocalPose.Transforms(poses.AsSpan().Slice(copy.Range.Start, copy.Range.Count))));
                    }
                    var output = (Index: body.Output.Index, Basis: body.Basis, Weight: body.Weight, Sampled: body.Sampled);
                    world.ExecuteBufferWrite<PoseResult, (int Index, PoseResult Basis, float Weight, bool Sampled)>(body.Output.Entity,
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
                    AnimationGraph.Submit(world, row.Entity, in body.Placement, body.Sampled);
                }
        });
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
            AnimationLayers layers = world.Read<AnimationLayers>(body.Output.Entity);
            if (body.Placement.Layer < layers.Count) body.Weight *= layers.Items[body.Placement.Layer].Weight;
        }
        if (body.Weight == 0) return;
        var copy = (Values: body.Samples.Transforms, Range: body.Source.Range);
        world.ExecuteBufferRead<LocalPose, (BoneTransform[] Values, PoseRange Range)>(body.Source.Entity,
            ref copy, static (poses, ref copy) => LocalPose.Transforms(poses.AsSpan().Slice(copy.Range.Start, copy.Range.Count))
                .CopyTo(copy.Values));
        body.Targets.CopyTo(body.Samples.Targets, 0);
        body.PreferenceWeights.CopyTo(body.Samples.PreferenceWeights, 0);
        body.Samples.Settings = body.Settings;
        body.Samples.ReadParents();
        body.Sampled = true;
        jobs.Add(JobSystem.Schedule(new SolveBody(body.Samples), body.Samples.Accesses));
    }

    public void OnDestroy(ref PoseNodeContext context) => context.Frame.World.ReleaseQuery(_bodies);

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
