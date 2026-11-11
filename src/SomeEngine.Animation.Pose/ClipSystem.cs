using System.Numerics;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Animation.Pose;

/// <summary>Samples concrete clip leaves into their bound local-pose results during Composition.</summary>
public struct ClipSystem : ISystem<ImmediateSystemContext>
{
    private Query _clips;
    private Dictionary<AnimationClip, ClipSamplingRows> _groups;
    private List<JobHandle> _jobs;

    public void OnCreate(ref ImmediateSystemContext context)
    {
        _groups = [];
        _jobs = [];
        _clips = context.World.Query(AnimationGraph.WithTimeAccess(new QueryDescription()
            .WithStored<Timeline>().With<Timeline>().WithStored<Buffer<ClipInstance>>()
            .Optional<Buffer<LocalPose>>().Optional<Buffer<PoseResult>>()
            .Optional<Buffer<TimelineEvent>>().Optional<AnimationLayers>()));
    }

    public void OnUpdate(ref ImmediateSystemContext context)
    {
        foreach (ClipSamplingRows group in _groups.Values) group.Begin();
        _jobs.Clear();
        var frame = new ClipFrame(context.World, _groups);
        {
            using QueryScope read = _clips.Open();
            Visit(in read, ref frame);

            static void Visit(scoped in QueryScope rows, ref ClipFrame frame)
            {
                foreach (QueryChunk chunk in rows)
                {
                    var buffers0 = chunk.Has<Buffer<ClipInstance>>() ? chunk.GetBuffers<ClipInstance>() : default;
                    for (int rowIndex = 0; rowIndex < chunk.Count; rowIndex++)
                    {
                        foreach (ref ClipInstance instance in buffers0[rowIndex].AsSpan())
                        {
                            AnimationLeafTime time = AnimationGraph.ReadTime(chunk, rowIndex, in instance.Placement);
                            instance.Time = time;
                            instance.Active = time.Active;
                            instance.Sampled = false;
                            if (instance.Starts != time.Starts)
                            {
                                instance.Starts = time.Starts;
                                instance.Finished = false;
                                instance.PreviousTime = -1;
                                instance.PreviousMotion = Matrix4x4.Identity;
                            }
                            if (!time.Active) continue;
                            instance.Weight = time.Weight;
                            if (frame.World.Has<AnimationLayers>(instance.Output.Entity))
                            {
                                AnimationLayers layers = frame.World.ReadComponentData<AnimationLayers>(instance.Output.Entity);
                                if (instance.Placement.Layer < layers.Count) instance.Weight *= layers.Items[instance.Placement.Layer].Weight;
                            }
                            if (instance.Finished) continue;
                            ClipParameters parameters = instance.Definition.Parameters[instance.Index];
                            AnimationClip clip = parameters.Clip;
                            if (!frame.Groups.TryGetValue(clip, out ClipSamplingRows? group))
                            {
                                var bones = new short[clip.Skeleton.BoneCount];
                                for (int bone = 0; bone < bones.Length; bone++) bones[bone] = (short)bone;
                                group = new(PoseAnimation.GetScheduler(frame.World), clip, bones);
                                group.Begin();
                                frame.Groups.Add(clip, group);
                            }
                            float elapsed = parameters.Loop ? time.Time.Time : Math.Min(time.Time.Time, clip.Duration);
                            group.AddFull(parameters.Loop ? elapsed % clip.Duration : elapsed, instance.Samples, instance.SampleAccess);
                            instance.Sampled = true;
                        }
                    }
                }

            }
        }
        foreach (ClipSamplingRows group in _groups.Values)
            if (group.Count > 0) _jobs.Add(group.Schedule());
        foreach (JobHandle job in _jobs) job.Complete();
        World world = context.World;
        {
            using QueryScope read = _clips.Open();
            Visit(in read, ref world);

            static void Visit(scoped in QueryScope rows, ref World world)
            {
                foreach (QueryChunk chunk in rows)
                {
                    var buffers0 = chunk.Has<Buffer<ClipInstance>>() ? chunk.GetBuffers<ClipInstance>() : default;
                    for (int rowIndex = 0; rowIndex < chunk.Count; rowIndex++)
                    {
                        foreach (ref ClipInstance instance in buffers0[rowIndex].AsSpan())
                        {
                            if (!instance.Active) continue;
                            AnimationLeafTime time = instance.Time;
                            float weight = instance.Weight;
                            if (!instance.Sampled)
                            {
                                var held = (Index: instance.Output.Index, Weight: weight);
                                {
                                    using var results = world.GetBuffer<PoseResult>(instance.Output.Entity);
                                    Visit(results, ref held);

                                    static void Visit(DynamicBuffer<PoseResult> results, ref (int Index, float Weight) held)
                                    {
                                        results[held.Index].Weight = held.Weight;

                                    }
                                }
                                continue;
                            }
                            ClipParameters parameters = instance.Definition.Parameters[instance.Index];
                            AnimationClip clip = parameters.Clip;
                            float elapsed = parameters.Loop ? time.Time.Time : Math.Min(time.Time.Time, clip.Duration);
                            RootMotionDelta rootMotion = RootMotionDelta.Identity;
                            if (parameters.RootMotion == RootMotionMode.Extract && clip.RootMotionBone >= 0)
                                rootMotion = ((ClipRootMotion[])instance.Definition.Data!)[instance.Index]
                                    .Extract(in parameters, ref instance, elapsed);
                            foreach (ClipEvent marker in clip.Events)
                            {
                                uint count = CrossedEvents(instance.PreviousTime, elapsed, marker.Time, clip.Duration, parameters.Loop);
                                if (count > 0) Animation.EmitEvent(world, chunk.Entities[rowIndex], marker.Id, count);
                            }
                            instance.PreviousTime = elapsed;
                            var sample = (Values: instance.Samples, Range: instance.Output.Range);
                            {
                                using var values = world.GetBuffer<LocalPose>(instance.Output.Entity);
                                Visit(values, ref sample);

                                static void Visit(DynamicBuffer<LocalPose> values, ref (BoneTransform[] Values, PoseRange Range) sample)
                                {
                                    sample.Values.AsSpan().CopyTo(LocalPose.Transforms(values.AsSpan().Slice(sample.Range.Start, sample.Range.Count)));

                                }
                            }
                            var output = (Index: instance.Output.Index, Time: elapsed, Phase: (double)elapsed / clip.Duration,
                                Weight: weight, Starts: time.Starts, Motion: rootMotion);
                            {
                                using var results = world.GetBuffer<PoseResult>(instance.Output.Entity);
                                Visit(results, ref output);

                                static void Visit(DynamicBuffer<PoseResult> results, ref (int Index, float Time, double Phase, float Weight, uint Starts, RootMotionDelta Motion) output)
                                {
                                    ref PoseResult result = ref results[output.Index];
                                    result.Time = output.Time;
                                    result.Phase = output.Phase;
                                    result.Weight = output.Weight;
                                    result.Starts = output.Starts;
                                    result.RootMotion = new(output.Motion.Transform * result.RootMotion.Transform);
                                    result.Version++;

                                }
                            }
                            instance.Finished = !parameters.Loop && elapsed >= clip.Duration;
                            AnimationGraph.Submit(chunk, rowIndex, in instance.Placement, !instance.Finished);
                        }
                    }
                }

            }
        }
    }

    public void OnDestroy(ref ImmediateSystemContext context) { }

    internal static uint CrossedEvents(float previous, float current, float marker, float duration, bool loop)
    {
        if (!loop) return marker > previous && marker <= current ? 1u : 0u;
        uint before = previous < marker ? 0 : (uint)MathF.Floor((previous - marker) / duration) + 1;
        uint after = current < marker ? 0 : (uint)MathF.Floor((current - marker) / duration) + 1;
        return after - before;
    }

    private readonly record struct ClipFrame(World World, Dictionary<AnimationClip, ClipSamplingRows> Groups);
}
