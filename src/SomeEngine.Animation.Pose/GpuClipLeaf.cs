using System.Numerics;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Animation.Pose;

/// <summary>A GPU pose producer's controls, copied through normal render extraction.</summary>
public struct GpuPoseTarget : ITrackedComponent
{
    public SkeletonInstance Skeleton;
    public QuantizedClipData? Clip;
    public GpuPoseStateMachine? StateMachine;
    public GpuPoseBlend? Blend;
    public GpuPoseRig? Rig;
    public GpuPoseMovement Movement;
    /// <summary>The current actor model frame in world space, also used by GPU secondary chains.</summary>
    public Matrix4x4 ModelToWorld;
    public Vector3 ModelOffset;
    public float[]? Controls;
    public float PlaybackTime;
    public bool Playing;
    public float SampleTime;
    public double CalculationTime;
    public uint Starts;
    /// <summary>The graph installation's start marker, retained across nested motion entries.</summary>
    public uint AnimationStarts;
    public ulong InputNumber;
    public CpuBoneDemand? CpuBones;
    public ReadOnlyMemory<GpuPoseReadbackRequest> Readbacks { get; internal set; }
    public GpuPoseEvaluation? Evaluation;
    public RootMotionDelta RootMotion;
    public bool ExtractRootMotion;
    public BoneTransform RootCorrection;
}

public readonly record struct GpuClipParameters(QuantizedClipData Clip, bool Loop = false,
    RootMotionMode RootMotion = RootMotionMode.SourcePose);

public struct GpuClipInstance : IBufferElement
{
    public AnimationLeafBatch<GpuClipParameters> Definition;
    public int Index;
    public AnimationLeafPlacement Placement;
    public Entity Target;
    public uint Starts;
    public bool Finished;
    internal GpuClipSignals Signals;
}

/// <summary>Uses the common graph clock to select GPU samples for one skeleton section.</summary>
public readonly struct GpuClipLeaf : IAnimationLeaf<GpuClipParameters>
{
    public static object Prepare(ReadOnlySpan<GpuClipParameters> parameters)
    {
        var signals = new GpuClipSignalDefinition[parameters.Length];
        for (int index = 0; index < signals.Length; index++) signals[index] = new(parameters[index]);
        return signals;
    }
    public static MotionBoundary Describe(in GpuClipParameters parameters) => parameters.Loop
        ? new(StageEnd.Settled) : new(StageEnd.Duration, parameters.Clip.Duration);

    public static void Install(World world, Entity animation, Entity target,
        AnimationLeafBatch<GpuClipParameters> definitions, int index, in AnimationLeafPlacement placement)
    {
        if (!world.Has<Buffer<GpuClipInstance>>(animation)) world.AddBuffer<GpuClipInstance>(animation);
        var instance = new GpuClipInstance
        {
            Definition = definitions,
            Index = index,
            Placement = placement,
            Target = target,
            Signals = ((GpuClipSignalDefinition[])definitions.Data!)[index].Bind(world.ReadComponentData<GpuPoseTarget>(target).Skeleton.Skeleton)
        };
        if (!world.Has<Buffer<TimelineEvent>>(animation)) world.AddBuffer<TimelineEvent>(animation);
        AnimationClip signalClip = instance.Signals.Definition.Clip;
        {
            using var events = world.GetBuffer<TimelineEvent>(animation);
            Visit(events, ref signalClip);

            static void Visit(DynamicBuffer<TimelineEvent> events, ref AnimationClip clip)
            {
                foreach (ClipEvent marker in clip.Events)
                {
                    bool found = false;
                    foreach (TimelineEvent current in events.AsSpan()) found |= current.Id == marker.Id;
                    if (!found) events.Add(new TimelineEvent { Id = marker.Id });
                }

            }
        }
        {
            using var instances = world.GetBuffer<GpuClipInstance>(animation);
            Visit(instances, ref instance);

            static void Visit(DynamicBuffer<GpuClipInstance> instances, ref GpuClipInstance instance)
            {
                if (instances.Count > 0 && instances[0].Placement.Starts != instance.Placement.Starts) instances.Clear();
                instances.Add(instance);

            }
        }
        GpuPoseTarget output = world.ReadComponentData<GpuPoseTarget>(target);
        output.Clip = definitions.Parameters[index].Clip;
        output.StateMachine = null;
        output.Blend = null;
        output.Rig = null;
        output.SampleTime = 0;
        output.Starts = placement.Starts;
        output.AnimationStarts = placement.Starts;
        world.Set(target, output);
    }

    public static void Release(World world, Entity animation)
    {
        using var instances = world.GetBuffer<GpuClipInstance>(animation);
        instances.Clear();

    }
}

public static partial class GpuPoseAnimation
{
    public static Entity CreateTarget(World world, SkeletonInstance skeleton)
    {
        Entity target = world.Create(new GpuPoseTarget { Skeleton = skeleton, ModelToWorld = Matrix4x4.Identity });
        CpuBoneDemands.For(world).Changed = true;
        return target;
    }

    public static void AddSystems(SystemGroup<ImmediateSystemContext> systems)
    {
        AnimationSystems.Add(systems, new GpuPoseFrameSystem(), AnimationPhase.CompositionInputs);
        AnimationSystems.Add(systems, new GpuClipSystem(), AnimationPhase.CompositionInputs);
        AnimationSystems.Add(systems, new GpuStateSystem(), AnimationPhase.CompositionInputs);
        AnimationSystems.Add(systems, new GpuBlendSystem(), AnimationPhase.CompositionInputs);
        AnimationSystems.Add(systems, new GpuRigSystem(), AnimationPhase.CompositionInputs);
        AnimationSystems.Add(systems, new CpuBoneSystem(), AnimationPhase.Completion);
    }
}

internal struct GpuPoseFrameSystem : ISystem<ImmediateSystemContext>
{
    private Query _targets;
    private double _time;
    public void OnCreate(ref ImmediateSystemContext context) =>
        _targets = context.World.Query(new QueryDescription().WithStored<GpuPoseTarget>());

    public void OnUpdate(ref ImmediateSystemContext context)
    {
        _time += context.DeltaTime;
        {
            using QueryScope read = _targets.Open();
            Visit(in read, ref _time);

            static void Visit(scoped in QueryScope rows, ref double time)
            {
                foreach (QueryChunk chunk in rows)
                    foreach (ref GpuPoseTarget target in chunk.Write<GpuPoseTarget>())
                    {
                        target.CalculationTime = time;
                        target.InputNumber++;
                        target.Playing = false;
                        target.RootMotion = RootMotionDelta.Identity;
                    }

            }
        }
    }
    public void OnDestroy(ref ImmediateSystemContext context) { }
}

internal struct GpuClipSystem : ISystem<ImmediateSystemContext>
{
    private Query _clips;
    public void OnCreate(ref ImmediateSystemContext context) =>
        _clips = context.World.Query(AnimationGraph.WithTimeAccess(new QueryDescription()
            .WithStored<Timeline>().With<Timeline>().WithStored<Buffer<GpuClipInstance>>()
            .Optional<GpuPoseTarget>().Optional<Buffer<TimelineEvent>>()));

    public void OnUpdate(ref ImmediateSystemContext context)
    {
        World world = context.World;
        {
            using QueryScope read = _clips.Open();
            Visit(in read, ref world);

            static void Visit(scoped in QueryScope rows, ref World world)
            {
                foreach (QueryChunk chunk in rows)
                {
                    var timelines = chunk.Write<Timeline>();
                    var clips = chunk.GetBuffers<GpuClipInstance>();
                    var times = AnimationGraph.ReadTimeColumn(chunk);
                    for (int row = 0; row < chunk.Count; row++)
                    {
                        ref Timeline timeline = ref timelines[row];
                        foreach (ref GpuClipInstance instance in clips[row].AsSpan())
                        {
                            AnimationLeafTime time = times.Read(row, in timeline, in instance.Placement);
                            if (instance.Starts != time.Starts) { instance.Starts = time.Starts; instance.Finished = false; instance.Signals.Reset(); }
                            if (!time.Active || instance.Finished) continue;
                            GpuClipParameters parameters = instance.Definition.Parameters[instance.Index];
                            float elapsed = parameters.Loop ? time.Time.Time : Math.Min(time.Time.Time, parameters.Clip.Duration);
                            GpuPoseTarget output = world.ReadComponentData<GpuPoseTarget>(instance.Target);
                            output.Clip = parameters.Clip;
                            output.StateMachine = null;
                            output.Blend = null;
                            output.Rig = null;
                            output.SampleTime = parameters.Loop ? elapsed % parameters.Clip.Duration : elapsed;
                            output.Starts = time.Starts;
                            instance.Signals.Update(world, chunk.Entities[row], in parameters, elapsed, ref output);
                            world.Set(instance.Target, output);
                            instance.Finished = !parameters.Loop && elapsed >= parameters.Clip.Duration;
                            times.Submit(row, ref timeline, in instance.Placement, !instance.Finished);
                        }
                    }
                }

            }
        }
    }
    public void OnDestroy(ref ImmediateSystemContext context) { }
}
