using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Animation.Pose;

/// <summary>Completes concrete weighted bone jobs before publishing their dependent pose results.</summary>
public struct PoseBlendSystem : ISystem<ImmediateSystemContext>
{
    private QueryHandle _blends;
    private List<JobHandle> _jobs;

    public void OnCreate(ref ImmediateSystemContext context)
    {
        _jobs = [];
        _blends = context.World.Query(AnimationGraph.WithTimeAccess(context.World.QueryDefinition()
            .ReadWrite<Timeline>().Enabled<Timeline>().WriteBuffer<PoseBlendInstance>()
            .Optional<GraphParameters>(QueryAccess.Read).Optional<AnimationLayers>(QueryAccess.Read)
            .OptionalBuffer<LocalPose>(QueryAccess.ReadWrite).OptionalBuffer<PoseResult>(QueryAccess.ReadWrite)));
    }

    public void OnUpdate(ref ImmediateSystemContext context)
    {
        var frame = new BlendFrame { World = context.World, Jobs = _jobs };
        do
        {
            _jobs.Clear();
            frame.NextLevel = int.MaxValue;
            context.World.ExecuteQuery(_blends, ref frame, static (QueryCursor rows, ref BlendFrame frame) =>
            {
                foreach (QueryRow row in rows.Rows)
                    foreach (ref PoseBlendInstance blend in row.Buffer<PoseBlendInstance>().AsSpan())
                    {
                        if (blend.Level > frame.Level) frame.NextLevel = Math.Min(frame.NextLevel, blend.Level);
                        if (blend.Level == frame.Level) Gather(frame.World, row.Entity, ref blend, frame.Jobs);
                    }
            });
            foreach (JobHandle job in _jobs) job.Complete();
            context.World.ExecuteQuery(_blends, ref frame, static (QueryCursor rows, ref BlendFrame frame) =>
            {
                World world = frame.World;
                foreach (QueryRow row in rows.Rows)
                    foreach (ref PoseBlendInstance blend in row.Buffer<PoseBlendInstance>().AsSpan())
                    {
                        if (!blend.Active || blend.Level != frame.Level) continue;
                        if (blend.Sampled)
                        {
                            var copy = (Values: blend.Samples.Output, Range: blend.Output.Range);
                            world.ExecuteBufferWrite<LocalPose, (BoneTransform[] Values, PoseRange Range)>(blend.Output.Entity,
                                ref copy, static (poses, ref copy) => copy.Values.AsSpan(0, copy.Range.Count)
                                    .CopyTo(LocalPose.Transforms(poses.AsSpan().Slice(copy.Range.Start, copy.Range.Count))));
                        }
                        var value = (Index: blend.Output.Index, Time: blend.Time, Phase: blend.Phase, Weight: blend.Weight,
                            Starts: blend.Starts, Sampled: blend.Sampled, Motion: blend.Samples.Output[^1]);
                        world.ExecuteBufferWrite<PoseResult, (int Index, double Time, double Phase, float Weight,
                            uint Starts, bool Sampled, BoneTransform Motion)>(blend.Output.Entity, ref value,
                            static (results, ref value) =>
                            {
                                ref PoseResult result = ref results[value.Index];
                                result.Weight = value.Weight;
                                if (!value.Sampled) return;
                                result.Time = value.Time;
                                result.Phase = value.Phase;
                                result.Starts = value.Starts;
                                result.RootMotion = new(value.Motion.ToMatrix() * result.RootMotion.Transform);
                                result.Version++;
                            });
                        AnimationGraph.Submit(world, row.Entity, in blend.Placement, blend.Running);
                    }
            });
            frame.Level = frame.NextLevel;
        } while (frame.Level != int.MaxValue);
    }

    private static void Gather(World world, Entity animation, ref PoseBlendInstance blend, List<JobHandle> jobs)
    {
        AnimationLeafTime time = AnimationGraph.ReadTime(world, animation, in blend.Placement);
        blend.Active = time.Active;
        blend.Sampled = false;
        if (blend.Starts != time.Starts)
        {
            blend.Starts = time.Starts;
            blend.Finished = false;
        }
        if (!time.Active) return;
        blend.Weight = time.Weight;
        if (world.Has<AnimationLayers>(blend.Output.Entity))
        {
            AnimationLayers layers = world.Read<AnimationLayers>(blend.Output.Entity);
            if (blend.Placement.Layer < layers.Count) blend.Weight *= layers.Items[blend.Placement.Layer].Weight;
        }
        if (blend.Finished) return;
        PoseBlendParameters parameters = blend.Definition.Parameters[blend.Index];
        GraphParameters graph = world.Read<GraphParameters>(animation);
        float total = 0;
        blend.Time = 0;
        blend.Phase = 0;
        for (int input = 0; input < blend.Inputs.Length; input++)
        {
            PoseBlendInput source = parameters.Blend.Inputs[input];
            PoseResultReference reference = blend.Inputs[input];
            PoseResult result = PoseAnimation.ReadResult(world, in reference);
            float weight = source.Weight * result.Weight;
            if (source.Parameter >= 0) weight *= graph.Values[source.Parameter];
            blend.Samples.Weights[input] = weight;
            total += weight;
            blend.Time += result.Time * weight;
            blend.Phase += result.Phase * weight;
            if (weight == 0) continue;
            int start = input * blend.Samples.Stride;
            var copy = (Values: blend.Samples.Inputs, Start: start, Range: reference.Range);
            world.ExecuteBufferRead<LocalPose, (BoneTransform[] Values, int Start, PoseRange Range)>(reference.Entity,
                ref copy, static (poses, ref copy) => LocalPose.Transforms(poses.AsSpan().Slice(copy.Range.Start, copy.Range.Count))
                    .CopyTo(copy.Values.AsSpan(copy.Start, copy.Range.Count)));
            blend.Samples.Inputs[start + reference.Range.Count] = new BoneTransform
            {
                Translation = result.RootMotion.Translation, Rotation = result.RootMotion.Rotation, Scale = Vector3.One,
            };
        }
        blend.Finished = parameters.Boundary.End == StageEnd.Duration && time.Time.Time >= parameters.Boundary.Duration;
        blend.Running = !blend.Finished && total > 0;
        if (total == 0)
        {
            blend.Weight = 0;
            return;
        }
        blend.Time /= total;
        blend.Phase /= total;
        blend.Sampled = true;
        jobs.Add(JobSystem.Schedule(new BlendBones(blend.Samples),
            [JobResourceAccess.Read(blend.Samples.Inputs), JobResourceAccess.Read(blend.Samples.Weights),
             JobResourceAccess.Write(blend.Samples.Output)]));
    }

    public void OnDestroy(ref ImmediateSystemContext context) => context.World.ReleaseQuery(_blends);

    private struct BlendFrame
    {
        internal World World;
        internal List<JobHandle> Jobs;
        internal int Level, NextLevel;
    }

    private readonly record struct BlendBones(PoseBlendSamples Samples) : IJob
    {
        public void Execute() => BoneTransformOps.Weighted(Samples.Inputs, Samples.Stride, Samples.Weights, Samples.Output);
    }
}
