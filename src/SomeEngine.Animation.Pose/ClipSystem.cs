using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Animation.Pose;

/// <summary>Samples concrete clip leaves into their bound local-pose results during Composition.</summary>
public struct ClipSystem : ISystem<ImmediateSystemContext>
{
    private QueryHandle _clips;
    private Dictionary<AnimationClip, ClipSamplingRows> _groups;
    private List<JobHandle> _jobs;

    public void OnCreate(ref ImmediateSystemContext context)
    {
        _groups = [];
        _jobs = [];
        _clips = context.World.Query(AnimationGraph.WithTimeAccess(context.World.QueryDefinition()
            .ReadWrite<Timeline>().Enabled<Timeline>().WriteBuffer<ClipInstance>()
            .OptionalBuffer<LocalPose>(QueryAccess.ReadWrite).OptionalBuffer<PoseResult>(QueryAccess.ReadWrite)
            .OptionalBuffer<TimelineEvent>(QueryAccess.ReadWrite).Optional<AnimationLayers>(QueryAccess.Read)));
    }

    public void OnUpdate(ref ImmediateSystemContext context)
    {
        foreach (ClipSamplingRows group in _groups.Values) group.Begin();
        _jobs.Clear();
        var frame = new ClipFrame(context.World, _groups);
        frame.World.ExecuteQuery(_clips, ref frame, static (scoped QueryCursor rows, ref ClipFrame frame) =>
        {
            foreach (QueryRow row in rows.Rows)
                foreach (ref ClipInstance instance in row.Buffer<ClipInstance>().AsSpan())
                {
                    AnimationLeafTime time = AnimationGraph.ReadTime(in row, in instance.Placement);
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
                        AnimationLayers layers = frame.World.Read<AnimationLayers>(instance.Output.Entity);
                        if (instance.Placement.Layer < layers.Count) instance.Weight *= layers.Items[instance.Placement.Layer].Weight;
                    }
                    if (instance.Finished) continue;
                    ClipParameters parameters = instance.Definition.Parameters[instance.Index];
                    AnimationClip clip = parameters.Clip;
                    if (!frame.Groups.TryGetValue(clip, out ClipSamplingRows? group))
                    {
                        var bones = new short[clip.Skeleton.BoneCount];
                        for (int bone = 0; bone < bones.Length; bone++) bones[bone] = (short)bone;
                        group = new(clip, bones);
                        group.Begin();
                        frame.Groups.Add(clip, group);
                    }
                    float elapsed = parameters.Loop ? time.Time.Time : Math.Min(time.Time.Time, clip.Duration);
                    group.AddFull(parameters.Loop ? elapsed % clip.Duration : elapsed, instance.Samples, instance.SampleAccess);
                    instance.Sampled = true;
                }
        });
        foreach (ClipSamplingRows group in _groups.Values)
            if (group.Count > 0) _jobs.Add(group.Schedule());
        foreach (JobHandle job in _jobs) job.Complete();
        World world = context.World;
        world.ExecuteQuery(_clips, ref world, static (scoped QueryCursor rows, ref World world) =>
        {
            foreach (QueryRow row in rows.Rows)
                foreach (ref ClipInstance instance in row.Buffer<ClipInstance>().AsSpan())
                {
                    if (!instance.Active) continue;
                    AnimationLeafTime time = instance.Time;
                    float weight = instance.Weight;
                    if (!instance.Sampled)
                    {
                        var held = (Index: instance.Output.Index, Weight: weight);
                        world.ExecuteBufferWrite<PoseResult, (int Index, float Weight)>(instance.Output.Entity, ref held,
                            static (results, ref held) => results[held.Index].Weight = held.Weight);
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
                        if (count > 0) Animation.EmitEvent(world, row.Entity, marker.Id, count);
                    }
                    instance.PreviousTime = elapsed;
                    var sample = (Values: instance.Samples, Range: instance.Output.Range);
                    world.ExecuteBufferWrite<LocalPose, (BoneTransform[] Values, PoseRange Range)>(instance.Output.Entity,
                        ref sample, static (values, ref sample) =>
                            sample.Values.AsSpan().CopyTo(LocalPose.Transforms(values.AsSpan().Slice(sample.Range.Start, sample.Range.Count))));
                    var output = (Index: instance.Output.Index, Time: elapsed, Phase: (double)elapsed / clip.Duration,
                        Weight: weight, Starts: time.Starts, Motion: rootMotion);
                    world.ExecuteBufferWrite<PoseResult, (int Index, float Time, double Phase, float Weight, uint Starts, RootMotionDelta Motion)>(instance.Output.Entity,
                        ref output, static (results, ref output) =>
                        {
                            ref PoseResult result = ref results[output.Index];
                            result.Time = output.Time;
                            result.Phase = output.Phase;
                            result.Weight = output.Weight;
                            result.Starts = output.Starts;
                            result.RootMotion = new(output.Motion.Transform * result.RootMotion.Transform);
                            result.Version++;
                        });
                    instance.Finished = !parameters.Loop && elapsed >= clip.Duration;
                    AnimationGraph.Submit(in row, in instance.Placement, !instance.Finished);
                }
        });
    }

    public void OnDestroy(ref ImmediateSystemContext context) => context.World.ReleaseQuery(_clips);

    internal static uint CrossedEvents(float previous, float current, float marker, float duration, bool loop)
    {
        if (!loop) return marker > previous && marker <= current ? 1u : 0u;
        uint before = previous < marker ? 0 : (uint)MathF.Floor((previous - marker) / duration) + 1;
        uint after = current < marker ? 0 : (uint)MathF.Floor((current - marker) / duration) + 1;
        return after - before;
    }

    private readonly record struct ClipFrame(World World, Dictionary<AnimationClip, ClipSamplingRows> Groups);
}
