using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Animation.Pose;

/// <summary>A GPU clip's instance controls, copied through normal render extraction.</summary>
public struct GpuPoseTarget : IComponent
{
    public SkeletonInstance Skeleton;
    public QuantizedClipData? Clip;
    public float SampleTime;
    public double CalculationTime;
    public uint Starts;
    public ulong InputNumber;
}

public readonly record struct GpuClipParameters(QuantizedClipData Clip, bool Loop = false);

[BufferCapacity(1)]
public struct GpuClipInstance : IBufferElement
{
    public AnimationLeafBatch<GpuClipParameters> Definition;
    public int Index;
    public AnimationLeafPlacement Placement;
    public Entity Target;
    public uint Starts;
    public bool Finished;
}

/// <summary>Uses the common graph clock to select GPU samples for one skeleton section.</summary>
public readonly struct GpuClipLeaf : IAnimationLeaf<GpuClipParameters>
{
    public static MotionBoundary Describe(in GpuClipParameters parameters) => parameters.Loop
        ? new(StageEnd.Settled) : new(StageEnd.Duration, parameters.Clip.Duration);

    public static void Install(World world, Entity animation, Entity target,
        AnimationLeafBatch<GpuClipParameters> definitions, int index, in AnimationLeafPlacement placement)
    {
        if (!world.HasBuffer<GpuClipInstance>(animation)) world.AddBuffer<GpuClipInstance>(animation);
        var instance = new GpuClipInstance { Definition = definitions, Index = index, Placement = placement, Target = target };
        world.ExecuteBufferWrite<GpuClipInstance, GpuClipInstance>(animation, ref instance,
            static (instances, ref instance) =>
            {
                if (instances.Count > 0 && instances.Read(0).Placement.Starts != instance.Placement.Starts) instances.Clear();
                instances.Add(instance);
            });
        GpuPoseTarget output = world.Read<GpuPoseTarget>(target);
        output.Clip = definitions.Parameters[index].Clip;
        output.SampleTime = 0;
        output.Starts = placement.Starts;
        world.Replace(target, output);
    }

    public static void Release(World world, Entity animation) =>
        world.ExecuteBufferWrite<GpuClipInstance>(animation, static instances => instances.Clear());
}

public static class GpuPoseAnimation
{
    public static Entity CreateTarget(World world, SkeletonInstance skeleton) =>
        world.CreateEntity(new GpuPoseTarget { Skeleton = skeleton });

    public static void AddSystems(SystemGroup<ImmediateSystemContext> systems)
    {
        AnimationSystems.Add(systems, new GpuPoseFrameSystem(), AnimationPhase.Composition);
        AnimationSystems.Add(systems, new GpuClipSystem(), AnimationPhase.Composition);
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
            .Optional<GpuPoseTarget>(QueryAccess.ReadWrite)));

    public void OnUpdate(ref ImmediateSystemContext context)
    {
        World world = context.World;
        world.ExecuteQuery(_clips, ref world, static (QueryCursor rows, ref World world) =>
        {
            foreach (QueryRow row in rows.Rows)
            foreach (ref GpuClipInstance instance in row.Buffer<GpuClipInstance>().AsSpan())
            {
                AnimationLeafTime time = AnimationGraph.ReadTime(world, row.Entity, in instance.Placement);
                if (instance.Starts != time.Starts) { instance.Starts = time.Starts; instance.Finished = false; }
                if (!time.Active || instance.Finished) continue;
                GpuClipParameters parameters = instance.Definition.Parameters[instance.Index];
                float elapsed = parameters.Loop ? time.Time.Time : Math.Min(time.Time.Time, parameters.Clip.Duration);
                GpuPoseTarget output = world.Read<GpuPoseTarget>(instance.Target);
                output.Clip = parameters.Clip;
                output.SampleTime = parameters.Loop ? elapsed % parameters.Clip.Duration : elapsed;
                output.Starts = time.Starts;
                world.Replace(instance.Target, output);
                instance.Finished = !parameters.Loop && elapsed >= parameters.Clip.Duration;
                AnimationGraph.Submit(world, row.Entity, in instance.Placement, !instance.Finished);
            }
        });
    }
    public void OnDestroy(ref ImmediateSystemContext context) => context.World.ReleaseQuery(_clips);
}
