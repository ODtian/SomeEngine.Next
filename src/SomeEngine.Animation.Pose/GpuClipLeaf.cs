using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Animation.Pose;

/// <summary>A GPU pose producer's controls, copied through normal render extraction.</summary>
public struct GpuPoseTarget : IComponent
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

[BufferCapacity(1)]
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
        if (!world.HasBuffer<GpuClipInstance>(animation)) world.AddBuffer<GpuClipInstance>(animation);
        var instance = new GpuClipInstance { Definition = definitions, Index = index, Placement = placement, Target = target,
            Signals = ((GpuClipSignalDefinition[])definitions.Data!)[index].Bind(world.Read<GpuPoseTarget>(target).Skeleton.Skeleton) };
        if (!world.HasBuffer<TimelineEvent>(animation)) world.AddBuffer<TimelineEvent>(animation);
        AnimationClip signalClip = instance.Signals.Definition.Clip;
        world.ExecuteBufferWrite<TimelineEvent, AnimationClip>(animation, ref signalClip, static (events, ref clip) =>
        {
            foreach (ClipEvent marker in clip.Events)
            {
                bool found = false;
                foreach (TimelineEvent current in events.AsSpan()) found |= current.Id == marker.Id;
                if (!found) events.Add(new TimelineEvent { Id = marker.Id });
            }
        });
        world.ExecuteBufferWrite<GpuClipInstance, GpuClipInstance>(animation, ref instance,
            static (instances, ref instance) =>
            {
                if (instances.Count > 0 && instances.Read(0).Placement.Starts != instance.Placement.Starts) instances.Clear();
                instances.Add(instance);
            });
        GpuPoseTarget output = world.Read<GpuPoseTarget>(target);
        output.Clip = definitions.Parameters[index].Clip;
        output.StateMachine = null;
        output.Blend = null;
        output.Rig = null;
        output.SampleTime = 0;
        output.Starts = placement.Starts;
        output.AnimationStarts = placement.Starts;
        world.Replace(target, output);
    }

    public static void Release(World world, Entity animation) =>
        world.ExecuteBufferWrite<GpuClipInstance>(animation, static instances => instances.Clear());
}

public static partial class GpuPoseAnimation
{
    public static Entity CreateTarget(World world, SkeletonInstance skeleton)
    {
        Entity target = world.CreateEntity(new GpuPoseTarget { Skeleton = skeleton, ModelToWorld = Matrix4x4.Identity });
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
    private QueryHandle _targets;
    private double _time;
    public void OnCreate(ref ImmediateSystemContext context) =>
        _targets = context.World.Query(new QueryDefinitionBuilder().ReadWrite<GpuPoseTarget>());

    public void OnUpdate(ref ImmediateSystemContext context)
    {
        _time += context.DeltaTime;
        context.World.ExecuteQuery(_targets, ref _time, static (QueryCursor rows, ref double time) =>
        {
            foreach (QueryRow row in rows.Rows)
            {
                ref GpuPoseTarget target = ref row.ReadWrite<GpuPoseTarget>();
                target.CalculationTime = time;
                target.InputNumber++;
                target.Playing = false;
                target.RootMotion = RootMotionDelta.Identity;
            }
        });
    }
    public void OnDestroy(ref ImmediateSystemContext context) => context.World.ReleaseQuery(_targets);
}

internal struct GpuClipSystem : ISystem<ImmediateSystemContext>
{
    private QueryHandle _clips;
    public void OnCreate(ref ImmediateSystemContext context) =>
        _clips = context.World.Query(AnimationGraph.WithTimeAccess(context.World.QueryDefinition()
            .ReadWrite<Timeline>().Enabled<Timeline>().WriteBuffer<GpuClipInstance>()
            .Optional<GpuPoseTarget>(QueryAccess.ReadWrite).OptionalBuffer<TimelineEvent>(QueryAccess.ReadWrite)));

    public void OnUpdate(ref ImmediateSystemContext context)
    {
        World world = context.World;
        world.ExecuteQuery(_clips, ref world, static (QueryCursor rows, ref World world) =>
        {
            foreach (QueryRow row in rows.Rows)
            foreach (ref GpuClipInstance instance in row.Buffer<GpuClipInstance>().AsSpan())
            {
                AnimationLeafTime time = AnimationGraph.ReadTime(in row, in instance.Placement);
                if (instance.Starts != time.Starts) { instance.Starts = time.Starts; instance.Finished = false; instance.Signals.Reset(); }
                if (!time.Active || instance.Finished) continue;
                GpuClipParameters parameters = instance.Definition.Parameters[instance.Index];
                float elapsed = parameters.Loop ? time.Time.Time : Math.Min(time.Time.Time, parameters.Clip.Duration);
                GpuPoseTarget output = world.Read<GpuPoseTarget>(instance.Target);
                output.Clip = parameters.Clip;
                output.StateMachine = null;
                output.Blend = null;
                output.Rig = null;
                output.SampleTime = parameters.Loop ? elapsed % parameters.Clip.Duration : elapsed;
                output.Starts = time.Starts;
                instance.Signals.Update(world, row.Entity, in parameters, elapsed, ref output);
                world.Replace(instance.Target, output);
                instance.Finished = !parameters.Loop && elapsed >= parameters.Clip.Duration;
                AnimationGraph.Submit(in row, in instance.Placement, !instance.Finished);
            }
        });
    }
    public void OnDestroy(ref ImmediateSystemContext context) => context.World.ReleaseQuery(_clips);
}
