using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.Job;

namespace SomeEngine.Animation.Pose;

public readonly record struct ClipParameters(AnimationClip Clip, PoseResultId Output,
    bool Loop = false, RootMotionMode RootMotion = RootMotionMode.SourcePose);

/// <summary>Concrete clip parameters, resolved output and playback history for one installed leaf.</summary>
public struct ClipInstance : IBufferElement
{
    public AnimationLeafBatch<ClipParameters> Definition;
    public int Index;
    public AnimationLeafPlacement Placement;
    public PoseResultReference Output;
    public uint Starts;
    public bool Finished;
    public BoneTransform[] Samples;
    public float PreviousTime;
    public Matrix4x4 PreviousMotion;
    internal AnimationLeafTime Time;
    internal float Weight;
    internal bool Active, Sampled;
    internal JobResourceAccess SampleAccess;
}

public readonly struct ClipLeaf : IAnimationLeaf<ClipParameters>
{
    public static MotionBoundary Describe(in ClipParameters parameters) => parameters.Loop
        ? new(StageEnd.Settled) : new(StageEnd.Duration, parameters.Clip.Duration);

    public static object Prepare(ReadOnlySpan<ClipParameters> parameters)
    {
        var motion = new ClipRootMotion[parameters.Length];
        for (int index = 0; index < parameters.Length; index++)
            if (parameters[index].RootMotion == RootMotionMode.Extract && parameters[index].Clip.RootMotionBone >= 0)
                motion[index] = ClipRootMotion.Create(parameters[index].Clip);
        return motion;
    }

    public static void Install(World world, Entity animation, Entity target,
        AnimationLeafBatch<ClipParameters> definitions, int index, in AnimationLeafPlacement placement)
    {
        if (!world.Has<Buffer<ClipInstance>>(animation)) world.AddBuffer<ClipInstance>(animation);
        if (!world.Has<Buffer<TimelineEvent>>(animation)) world.AddBuffer<TimelineEvent>(animation);
        var instance = new ClipInstance
        {
            Definition = definitions,
            Index = index,
            Placement = placement,
            Output = PoseAnimation.Resolve(world, target, definitions.Parameters[index].Output),
            Samples = new BoneTransform[definitions.Parameters[index].Clip.Skeleton.BoneCount],
        };
        instance.SampleAccess = JobResourceAccess.Write(PoseAnimation.GetScheduler(world), instance.Samples);
        {
            using var instances = world.GetBuffer<ClipInstance>(animation);
            Visit(instances, ref instance);

            static void Visit(DynamicBuffer<ClipInstance> instances, ref ClipInstance instance)
            {
                if (instances.Count > 0 && instances[0].Placement.Starts != instance.Placement.Starts) instances.Clear();
                instances.Add(instance);

            }
        }
        AnimationClip clip = definitions.Parameters[index].Clip;
        {
            using var events = world.GetBuffer<TimelineEvent>(animation);
            Visit(events, ref clip);

            static void Visit(DynamicBuffer<TimelineEvent> events, ref AnimationClip clip)
            {
                events.EnsureCapacity(events.Count + clip.Events.Length);
                foreach (ClipEvent marker in clip.Events)
                {
                    bool exists = false;
                    foreach (TimelineEvent current in events.AsSpan())
                        if (current.Id == marker.Id) exists = true;
                    if (!exists) events.Add(new TimelineEvent { Id = marker.Id });
                }

            }
        }
    }

    public static void Release(World world, Entity animation)
    {
        using var instances = world.GetBuffer<ClipInstance>(animation);
        instances.Clear();

    }
}
