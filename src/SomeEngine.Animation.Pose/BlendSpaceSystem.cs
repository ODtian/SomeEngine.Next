using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Animation.Pose;

/// <summary>Advances each instance's shared cycle phase and samples its contributing clips in concrete jobs.</summary>
public struct BlendSpaceSystem : ISystem<ImmediateSystemContext>
{
    private QueryHandle _spaces;
    private List<JobHandle> _jobs;

    public void OnCreate(ref ImmediateSystemContext context)
    {
        _jobs = [];
        _spaces = context.World.Query(AnimationGraph.WithTimeAccess(context.World.QueryDefinition()
            .ReadWrite<Timeline>().Enabled<Timeline>().WriteBuffer<BlendSpaceInstance>().Read<GraphParameters>()
            .Optional<AnimationLayers>(QueryAccess.Read).OptionalBuffer<LocalPose>(QueryAccess.ReadWrite)
            .OptionalBuffer<PoseResult>(QueryAccess.ReadWrite)));
    }

    public void OnUpdate(ref ImmediateSystemContext context)
    {
        _jobs.Clear();
        var frame = (World: context.World, Jobs: _jobs);
        context.World.ExecuteQuery(_spaces, ref frame, static (QueryCursor rows, ref (World World, List<JobHandle> Jobs) frame) =>
        {
            foreach (QueryRow row in rows.Rows)
            {
                GraphParameters graph = row.Read<GraphParameters>();
                foreach (ref BlendSpaceInstance instance in row.Buffer<BlendSpaceInstance>().AsSpan())
                {
                    AnimationLeafTime time = AnimationGraph.ReadTime(frame.World, row.Entity, in instance.Placement);
                    instance.Active = time.Active;
                    if (instance.Starts != time.Starts)
                    {
                        instance.Starts = time.Starts;
                        instance.Samples.Phase = 0;
                    }
                    if (!time.Active) continue;
                    instance.Weight = time.Weight;
                    if (frame.World.Has<AnimationLayers>(instance.Output.Entity))
                    {
                        AnimationLayers layers = frame.World.Read<AnimationLayers>(instance.Output.Entity);
                        if (instance.Placement.Layer < layers.Count) instance.Weight *= layers.Items[instance.Placement.Layer].Weight;
                    }
                    BlendSpaceParameters parameters = instance.Definition.Parameters[instance.Index];
                    BlendSpace space = parameters.Space;
                    space.GetWeights(new(graph.Values[parameters.ParameterX], graph.Values[parameters.ParameterY]), instance.Samples.Weights);
                    double frequency = 0;
                    for (int sample = 0; sample < space.Samples.Length; sample++)
                        frequency += (double)instance.Samples.Weights[sample] / space.Samples[sample].Clip.Duration;
                    instance.Samples.Phase += time.Time.Delta * frequency;
                    instance.Time = time.Time.Time;
                    frame.Jobs.Add(PoseAnimation.GetScheduler(frame.World).Schedule(new SampleSpace(space, instance.Samples), instance.Samples.Accesses));
                }
            }
        });
        foreach (JobHandle job in _jobs) job.Complete();
        World world = context.World;
        world.ExecuteQuery(_spaces, ref world, static (QueryCursor rows, ref World world) =>
        {
            foreach (QueryRow row in rows.Rows)
                foreach (ref BlendSpaceInstance instance in row.Buffer<BlendSpaceInstance>().AsSpan())
                {
                    if (!instance.Active) continue;
                    var copy = (Values: instance.Samples.Output, Range: instance.Output.Range);
                    world.ExecuteBufferWrite<LocalPose, (BoneTransform[] Values, PoseRange Range)>(instance.Output.Entity,
                        ref copy, static (poses, ref copy) => copy.Values.AsSpan()
                            .CopyTo(LocalPose.Transforms(poses.AsSpan().Slice(copy.Range.Start, copy.Range.Count))));
                    var value = (Index: instance.Output.Index, Time: instance.Time, Phase: instance.Samples.Phase,
                        Weight: instance.Weight, Starts: instance.Starts);
                    world.ExecuteBufferWrite<PoseResult, (int Index, double Time, double Phase, float Weight, uint Starts)>(instance.Output.Entity,
                        ref value, static (results, ref value) =>
                        {
                            ref PoseResult result = ref results[value.Index];
                            result.Time = value.Time;
                            result.Phase = value.Phase;
                            result.Weight = value.Weight;
                            result.Starts = value.Starts;
                            result.Version++;
                        });
                    AnimationGraph.Submit(world, row.Entity, in instance.Placement, running: true);
                }
        });
    }

    public void OnDestroy(ref ImmediateSystemContext context) => context.World.ReleaseQuery(_spaces);

    private readonly record struct SampleSpace(BlendSpace Space, BlendSpaceSamples Samples) : IJob
    {
        public void Execute()
        {
            int bones = Samples.Output.Length;
            for (int sample = 0; sample < Space.Samples.Length; sample++)
            {
                if (Samples.Weights[sample] == 0) continue;
                BlendSpaceSample point = Space.Samples[sample];
                double phase = Samples.Phase + point.PhaseOffset;
                float time = (float)(phase - Math.Floor(phase)) * point.Clip.Duration;
                point.Clip.SampleLocal(time, Samples.Inputs.AsSpan(sample * bones, bones));
            }
            BoneTransformOps.Weighted(Samples.Inputs, bones, Samples.Weights, Samples.Output);
        }
    }
}
