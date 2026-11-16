using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Animation.Pose;

/// <summary>Advances each instance's shared cycle phase and samples its contributing clips in concrete jobs.</summary>
public struct BlendSpaceSystem : ISystem<ImmediateSystemContext>
{
    private Query _spaces;
    private List<JobHandle> _jobs;

    public void OnCreate(ref ImmediateSystemContext context)
    {
        _jobs = [];
        _spaces = context.World.Query(AnimationGraph.WithTimeAccess(new QueryDescription()
            .WithStored<Timeline>().With<Timeline>().WithStored<Buffer<BlendSpaceInstance>>().WithStored<GraphParameters>()
            .Optional<AnimationLayers>().Optional<Buffer<LocalPose>>()
            .Optional<Buffer<PoseResult>>()));
    }

    public void OnUpdate(ref ImmediateSystemContext context)
    {
        _jobs.Clear();
        var frame = (World: context.World, Jobs: _jobs);
        {
            using QueryScope read = _spaces.Open();
            Visit(in read, ref frame);

            static void Visit(scoped in QueryScope rows, ref (World World, List<JobHandle> Jobs) frame)
            {
                foreach (QueryChunk chunk in rows)
                {
                    chunk.TryRead<GraphParameters>(out var values0);
                    var buffers0 = chunk.Has<Buffer<BlendSpaceInstance>>() ? chunk.GetBuffers<BlendSpaceInstance>() : default;
                    for (int rowIndex = 0; rowIndex < chunk.Count; rowIndex++)
                    {

                        GraphParameters graph = values0[rowIndex];
                        foreach (ref BlendSpaceInstance instance in buffers0[rowIndex].AsSpan())
                        {
                            AnimationLeafTime time = AnimationGraph.ReadTime(frame.World, chunk.Entities[rowIndex], in instance.Placement);
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
                                AnimationLayers layers = frame.World.ReadValue<AnimationLayers>(instance.Output.Entity);
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
                }

            }
        }
        foreach (JobHandle job in _jobs) job.Complete();
        World world = context.World;
        {
            using QueryScope read = _spaces.Open();
            Visit(in read, ref world);

            static void Visit(scoped in QueryScope rows, ref World world)
            {
                foreach (QueryChunk chunk in rows)
                {
                    var buffers0 = chunk.Has<Buffer<BlendSpaceInstance>>() ? chunk.GetBuffers<BlendSpaceInstance>() : default;
                    for (int rowIndex = 0; rowIndex < chunk.Count; rowIndex++)
                    {
                        foreach (ref BlendSpaceInstance instance in buffers0[rowIndex].AsSpan())
                        {
                            if (!instance.Active) continue;
                            var copy = (Values: instance.Samples.Output, Range: instance.Output.Range);
                            {
                                using var poses = world.GetBuffer<LocalPose>(instance.Output.Entity);
                                Visit(poses, ref copy);

                                static void Visit(DynamicBuffer<LocalPose> poses, ref (BoneTransform[] Values, PoseRange Range) copy)
                                {
                                    copy.Values.AsSpan()
                                                                .CopyTo(LocalPose.Transforms(poses.AsSpan().Slice(copy.Range.Start, copy.Range.Count)));

                                }
                            }
                            var value = (Index: instance.Output.Index, Time: instance.Time, Phase: instance.Samples.Phase,
                                Weight: instance.Weight, Starts: instance.Starts);
                            {
                                using var results = world.GetBuffer<PoseResult>(instance.Output.Entity);
                                Visit(results, ref value);

                                static void Visit(DynamicBuffer<PoseResult> results, ref (int Index, double Time, double Phase, float Weight, uint Starts) value)
                                {
                                    ref PoseResult result = ref results[value.Index];
                                    result.Time = value.Time;
                                    result.Phase = value.Phase;
                                    result.Weight = value.Weight;
                                    result.Starts = value.Starts;
                                    result.Version++;

                                }
                            }
                            AnimationGraph.Submit(world, chunk.Entities[rowIndex], in instance.Placement, running: true);
                        }
                    }
                }

            }
        }
    }

    public void OnDestroy(ref ImmediateSystemContext context) { }

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
