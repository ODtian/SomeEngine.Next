using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Animation.Pose;

public readonly record struct GpuTwoBoneParameters(GpuTwoBonePose Pose, int TranslationBone = -1,
    float Duration = float.PositiveInfinity);
public readonly record struct GpuAimParameters(GpuAimPose Pose, int TranslationBone = -1,
    float Duration = float.PositiveInfinity);

public readonly struct GpuTwoBoneLeaf : IAnimationLeaf<GpuTwoBoneParameters>
{
    public static MotionBoundary Describe(in GpuTwoBoneParameters parameters) => GpuRigPlayback.Boundary(parameters.Duration);
    public static object Prepare(ReadOnlySpan<GpuTwoBoneParameters> parameters)
    {
        var rigs = new GpuPoseRig[parameters.Length];
        for (int index = 0; index < rigs.Length; index++) rigs[index] = new(parameters[index].Pose, parameters[index].TranslationBone);
        return rigs;
    }
    public static void Install(World world, Entity animation, Entity target,
        AnimationLeafBatch<GpuTwoBoneParameters> definitions, int index, in AnimationLeafPlacement placement) =>
        GpuRigPlayback.Install(world, animation, target, ((GpuPoseRig[])definitions.Data!)[index],
            definitions.Parameters[index].Duration, in placement);
    public static void Release(World world, Entity animation) => GpuRigPlayback.Release(world, animation);
}

public readonly struct GpuAimLeaf : IAnimationLeaf<GpuAimParameters>
{
    public static MotionBoundary Describe(in GpuAimParameters parameters) => GpuRigPlayback.Boundary(parameters.Duration);
    public static object Prepare(ReadOnlySpan<GpuAimParameters> parameters)
    {
        var rigs = new GpuPoseRig[parameters.Length];
        for (int index = 0; index < rigs.Length; index++) rigs[index] = new(parameters[index].Pose, parameters[index].TranslationBone);
        return rigs;
    }
    public static void Install(World world, Entity animation, Entity target,
        AnimationLeafBatch<GpuAimParameters> definitions, int index, in AnimationLeafPlacement placement) =>
        GpuRigPlayback.Install(world, animation, target, ((GpuPoseRig[])definitions.Data!)[index],
            definitions.Parameters[index].Duration, in placement);
    public static void Release(World world, Entity animation) => GpuRigPlayback.Release(world, animation);
}

[BufferCapacity(1)]
public struct GpuRigInstance : IBufferElement
{
    public GpuPoseRig Rig;
    public AnimationLeafPlacement Placement;
    public Entity Target;
    public float Duration;
    internal float[] Controls;
    internal uint Starts;
    internal bool Finished;
}

internal static class GpuRigPlayback
{
    internal static MotionBoundary Boundary(float duration) => float.IsPositiveInfinity(duration)
        ? new(StageEnd.Settled) : new(StageEnd.Duration, duration);

    internal static void Install(World world, Entity animation, Entity target, GpuPoseRig rig,
        float duration, in AnimationLeafPlacement placement)
    {
        if (!world.HasBuffer<GpuRigInstance>(animation)) world.AddBuffer<GpuRigInstance>(animation);
        if (!world.Has<GraphParameters>(animation)) world.Add(animation, new GraphParameters());
        if (!world.HasBuffer<GpuPoseGoal>(target)) world.AddBuffer<GpuPoseGoal>(target);
        int goals = rig.GoalCount;
        world.ExecuteBufferWrite<GpuPoseGoal, int>(target, ref goals, static (values, ref goals) =>
        {
            values.Clear(); values.EnsureCapacity(goals);
            for (int goal = 0; goal < goals; goal++) values.Add(default);
        });
        var instance = new GpuRigInstance
        {
            Rig = rig, Placement = placement, Target = target, Duration = duration,
            Controls = new float[rig.Source.InputCount],
        };
        world.ExecuteBufferWrite<GpuRigInstance, GpuRigInstance>(animation, ref instance, static (instances, ref instance) =>
        {
            if (instances.Count > 0 && instances.Read(0).Placement.Starts != instance.Placement.Starts) instances.Clear();
            instances.Add(instance);
        });
        GpuPoseTarget output = world.Read<GpuPoseTarget>(target);
        output.Clip = null; output.StateMachine = null;
        output.Blend = rig.Source; output.Rig = rig;
        output.Controls = instance.Controls;
        output.PlaybackTime = 0; output.Playing = false; output.Starts = placement.Starts;
        output.AnimationStarts = placement.Starts;
        world.Replace(target, output);
    }

    internal static void Release(World world, Entity animation) =>
        world.ExecuteBufferWrite<GpuRigInstance>(animation, static instances => instances.Clear());
}

internal struct GpuRigSystem : ISystem<ImmediateSystemContext>
{
    private QueryHandle _rigs;
    public void OnCreate(ref ImmediateSystemContext context) =>
        _rigs = context.World.Query(AnimationGraph.WithTimeAccess(context.World.QueryDefinition()
            .ReadWrite<Timeline>().Enabled<Timeline>().WriteBuffer<GpuRigInstance>().Read<GraphParameters>()
            .Optional<GpuPoseTarget>(QueryAccess.ReadWrite)));

    public void OnUpdate(ref ImmediateSystemContext context)
    {
        World world = context.World;
        world.ExecuteQuery(_rigs, ref world, static (QueryCursor rows, ref World world) =>
        {
            foreach (QueryRow row in rows.Rows)
            {
                GraphParameters controls = row.Read<GraphParameters>();
                foreach (ref GpuRigInstance instance in row.Buffer<GpuRigInstance>().AsSpan())
                {
                    AnimationLeafTime time = AnimationGraph.ReadTime(world, row.Entity, in instance.Placement);
                    if (instance.Starts != time.Starts) { instance.Starts = time.Starts; instance.Finished = false; }
                    if (!time.Active || instance.Finished) continue;
                    float elapsed = Math.Min(time.Time.Time, instance.Duration);
                    for (int input = 0; input < instance.Controls.Length; input++) instance.Controls[input] = controls.Values[input];
                    GpuPoseTarget output = world.Read<GpuPoseTarget>(instance.Target);
                    output.Clip = null; output.StateMachine = null;
                    output.Blend = instance.Rig.Source; output.Rig = instance.Rig;
                    output.Controls = instance.Controls;
                    output.PlaybackTime = elapsed; output.Playing = true; output.Starts = time.Starts;
                    world.Replace(instance.Target, output);
                    instance.Finished = elapsed >= instance.Duration;
                    AnimationGraph.Submit(world, row.Entity, in instance.Placement, !instance.Finished);
                }
            }
        });
    }
    public void OnDestroy(ref ImmediateSystemContext context) => context.World.ReleaseQuery(_rigs);
}
