using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Animation.Pose;

/// <summary>Samples concrete clip leaves into their bound local-pose results during Composition.</summary>
public struct ClipSystem : ISystem<ImmediateSystemContext>
{
    private QueryHandle _clips;

    public void OnCreate(ref ImmediateSystemContext context) =>
        _clips = context.World.Query(AnimationGraph.WithTimeAccess(context.World.QueryDefinition()
            .ReadWrite<Timeline>().Enabled<Timeline>().WriteBuffer<ClipInstance>()
            .OptionalBuffer<LocalPose>(QueryAccess.ReadWrite).OptionalBuffer<PoseResult>(QueryAccess.ReadWrite)
            .OptionalBuffer<TimelineEvent>(QueryAccess.ReadWrite).Optional<AnimationLayers>(QueryAccess.Read)));

    public void OnUpdate(ref ImmediateSystemContext context)
    {
        World world = context.World;
        world.ExecuteQuery(_clips, ref world, static (QueryCursor rows, ref World world) =>
        {
            foreach (QueryRow row in rows.Rows)
                foreach (ref ClipInstance instance in row.Buffer<ClipInstance>().AsSpan())
                {
                    AnimationLeafTime time = AnimationGraph.ReadTime(world, row.Entity, in instance.Placement);
                    if (instance.Starts != time.Starts)
                    {
                        instance.Starts = time.Starts;
                        instance.Finished = false;
                        instance.PreviousTime = -1;
                        instance.PreviousMotion = Matrix4x4.Identity;
                    }
                    if (!time.Active) continue;
                    float weight = time.Weight;
                    if (world.Has<AnimationLayers>(instance.Output.Entity))
                    {
                        AnimationLayers layers = world.Read<AnimationLayers>(instance.Output.Entity);
                        if (instance.Placement.Layer < layers.Count) weight *= layers.Items[instance.Placement.Layer].Weight;
                    }
                    if (instance.Finished)
                    {
                        var held = (Index: instance.Output.Index, Weight: weight);
                        world.ExecuteBufferWrite<PoseResult, (int Index, float Weight)>(instance.Output.Entity, ref held,
                            static (results, ref held) => results[held.Index].Weight = held.Weight);
                        continue;
                    }
                    ClipParameters parameters = instance.Definition.Parameters[instance.Index];
                    AnimationClip clip = parameters.Clip;
                    float elapsed = parameters.Loop ? time.Time.Time : Math.Min(time.Time.Time, clip.Duration);
                    float sampleTime = parameters.Loop ? elapsed % clip.Duration : elapsed;
                    JobHandle sampled = JobSystem.Schedule(new SampleClip(clip, sampleTime, instance.Samples),
                        [JobResourceAccess.Read(JobSystem.Default.GetResourceToken(clip)), JobResourceAccess.Write(instance.Samples)]);
                    sampled.Complete();
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
                    AnimationGraph.Submit(world, row.Entity, in instance.Placement, !instance.Finished);
                }
        });
    }

    public void OnDestroy(ref ImmediateSystemContext context) => context.World.ReleaseQuery(_clips);

    private static uint CrossedEvents(float previous, float current, float marker, float duration, bool loop)
    {
        if (!loop) return marker > previous && marker <= current ? 1u : 0u;
        uint before = previous < marker ? 0 : (uint)MathF.Floor((previous - marker) / duration) + 1;
        uint after = current < marker ? 0 : (uint)MathF.Floor((current - marker) / duration) + 1;
        return after - before;
    }

    private readonly record struct SampleClip(AnimationClip Clip, float Time, BoneTransform[] Destination) : IJob
    {
        public void Execute() => Clip.SampleLocal(Time, Destination);
    }
}
