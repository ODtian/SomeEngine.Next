using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Job;
using SomeEngine.Physics;

namespace SomeEngine.Animation.Pose;

/// <summary>Evaluates the original secondary-chain leaves in Game and delivers their completed device poses.</summary>
public sealed class DevicePoseSystem(IGraphicsBackend backend, Device device) : ISystem<ImmediateSystemContext>
{
    private QueryHandle _targets;
    private readonly List<(Entity Entity, GpuPoseTarget Input)> _inputs = [];
    private Entity[] _members = [];
    private DevicePosePhysics? _physics;
    private DevicePoseSources? _sources;
    private JobHandle _completion;

    public void OnCreate(ref ImmediateSystemContext context) =>
        _targets = context.World.Query(new QueryDefinitionBuilder().Read<GpuPoseTarget>());

    public void OnUpdate(ref ImmediateSystemContext context)
    {
        _inputs.Clear();
        DevicePoseSystem system = this;
        context.World.ExecuteReadQuery(_targets, ref system, static (scoped QueryCursor rows, ref DevicePoseSystem system) =>
        {
            foreach (QueryRow row in rows.Rows) system._inputs.Add((row.Entity, row.Read<GpuPoseTarget>()));
        });
        if (_sources is null)
        {
            int bodies = 0, lengths = 0, bends = 0;
            foreach (var source in _inputs)
                if (source.Input.Rig is { } rig)
                    foreach (GpuPoseSource step in rig.Steps)
                        if (step is GpuXpbdPose chain)
                        {
                            bodies += chain.Chain.Bones.Length;
                            lengths += chain.Chain.Bones.Length - 1;
                            bends += chain.Chain.Bones.Length - 2;
                        }
            if (bodies == 0) return;
            _physics = new(backend, device, context.World, new(bodies, Math.Max(1, lengths), Math.Max(1, bends)));
            int bones = 0, blendStates = 0, blendLocals = 0, controls = 0, goals = 0, operations = 0;
            foreach (var source in _inputs)
            {
                GpuPoseTarget input = source.Input;
                int count = input.Skeleton.Skeleton.BoneCount, states = input.Blend?.Steps.Length ?? 0;
                bones += count; blendStates += states; blendLocals += count * states;
                controls += input.Controls?.Length ?? 0; goals += input.Rig?.GoalCount ?? 0;
                operations += (input.Rig?.Steps.Length ?? 0) + (input.Rig?.TranslationBone >= 0 ? 1 : 0);
                if (HasChain(input.Rig)) _physics.Bind(source.Entity);
            }
            var represented = _inputs.Select(source => source.Input.Skeleton).ToHashSet();
            foreach (var source in _inputs)
                foreach (SkeletonInstance section in source.Input.Skeleton.Sections)
                    if (represented.Add(section)) bones += section.Skeleton.BoneCount;
            _sources = new(backend, device, context.World, _targets, DevicePoseShaders.Get(device.Capabilities.CompileTarget),
                new(_inputs.Count, bones, Math.Max(1, blendStates), Math.Max(1, blendLocals), Math.Max(1, controls),
                    0, Math.Max(1, goals), Math.Max(1, operations)), _physics);
        }
        Entity[] current = _inputs.Select(source => source.Entity).ToArray();
        foreach (Entity previous in _members) if (!current.Contains(previous)) _physics!.Remove(previous);
        foreach (var source in _inputs)
            if (HasChain(source.Input.Rig)) _physics!.Bind(source.Entity);
            else _physics!.Remove(source.Entity);
        _members = current;
        if (_inputs.Count == 0) return;
        double time = _inputs.Max(source => source.Input.CalculationTime);
        long number = unchecked((long)_inputs.Max(source => source.Input.InputNumber));
        var frame = new PhysicsStepContext(context.World, QueryDefinition.Empty, number, 0, 1, time - context.DeltaTime, context.DeltaTime);
        var stages = _physics!.Upload().Then(_sources.Evaluate()).Then(_sources.CopyOutput());
        _completion = stages.Schedule(in frame, _completion);
    }

    private static bool HasChain(GpuPoseRig? rig)
    {
        if (rig is null) return false;
        foreach (GpuPoseSource step in rig.Steps) if (step is GpuXpbdPose) return true;
        return false;
    }

    public void OnDestroy(ref ImmediateSystemContext context)
    {
        _completion.Complete(); _sources?.Dispose(); _physics?.Dispose();
        context.World.ReleaseQuery(_targets);
    }
}
