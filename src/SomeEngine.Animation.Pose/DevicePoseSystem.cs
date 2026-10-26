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
    private readonly List<(DevicePoseSources Sources, DevicePosePhysics Physics)> _retired = [];

    public void OnCreate(ref ImmediateSystemContext context) =>
        _targets = context.World.Query(new QueryDefinitionBuilder().Read<GpuPoseTarget>().Read<GpuPoseSourceState>());

    public void OnUpdate(ref ImmediateSystemContext context)
    {
        if (_retired.Count > 0)
        {
            _completion.Complete();
            foreach (var retired in _retired) { retired.Sources.Dispose(); retired.Physics.Dispose(); }
            _retired.Clear();
        }
        _inputs.Clear();
        DevicePoseSystem system = this;
        context.World.ExecuteReadQuery(_targets, ref system, static (scoped QueryCursor rows, ref DevicePoseSystem system) =>
        {
            foreach (QueryRow row in rows.Rows)
                if (row.Read<GpuPoseSourceState>().Sources is not { } selected || ReferenceEquals(selected, system._sources))
                    system._inputs.Add((row.Entity, row.Read<GpuPoseTarget>()));
        });
        Entity[] current = _inputs.Select(source => source.Entity).ToArray();
        if (_physics is not null)
        {
            HashSet<Entity> present = current.ToHashSet();
            foreach (Entity previous in _members) if (!present.Contains(previous)) _physics.Remove(previous);
            foreach (var source in _inputs)
                if (!HasChain(source.Input.Rig) || !_physics.HasRig(source.Entity, source.Input.Rig!)) _physics.Remove(source.Entity);
        }
        var required = RequiredCapacity();
        if (_sources is null && required.Physics.Bodies == 0) return;
        if (_sources is null || Grows(_sources.Capacity, required.Sources)
            || required.Physics.Bodies > _physics!.Bodies.Capacity || required.Physics.Lengths > _physics.Lengths.Capacity
            || required.Physics.Bends > _physics.Bends.Capacity)
        {
            _completion.Complete();
            DevicePosePhysics? previousPhysics = _physics;
            DevicePoseSources? previousSources = _sources;
            DevicePoseCapacity capacity = previousSources is null ? required.Sources : Grow(previousSources.Capacity, required.Sources);
            _physics = new(backend, device, context.World, new(
                Math.Max(required.Physics.Bodies, (previousPhysics?.Bodies.Capacity ?? 0) * 2),
                Math.Max(required.Physics.Lengths, (previousPhysics?.Lengths.Capacity ?? 0) * 2),
                Math.Max(required.Physics.Bends, (previousPhysics?.Bends.Capacity ?? 0) * 2)));
            if (previousPhysics is not null) _physics.ContinueFrom(previousPhysics);
            _sources = new(backend, device, context.World, _targets, DevicePoseShaders.Get(device.Capabilities.CompileTarget), capacity, _physics);
            if (previousSources is not null) _retired.Add((previousSources, previousPhysics!));
        }
        foreach (var source in _inputs)
            if (HasChain(source.Input.Rig)) _physics!.Bind(source.Entity);
        _members = current;
        _sources.SelectTargets(current);
        if (_inputs.Count == 0) return;
        double time = _inputs.Max(source => source.Input.CalculationTime);
        long number = unchecked((long)_inputs.Max(source => source.Input.InputNumber));
        var frame = new PhysicsStepContext(context.World, QueryDefinition.Empty, number, 0, 1, time - context.DeltaTime, context.DeltaTime);
        JobHandle continued = _physics!.ScheduleContinuation(in frame, _completion);
        var stages = _physics.Upload().Then(_sources.Evaluate()).Then(_sources.CopyOutput());
        _completion = stages.Schedule(in frame, continued);
    }

    private (DevicePosePhysicsCapacity Physics, DevicePoseCapacity Sources) RequiredCapacity()
    {
        int bodies = 0, lengths = 0, bends = 0, bones = 0, states = 0, locals = 0, controls = 0, goals = 0, operations = 0;
        foreach (var source in _inputs)
        {
            GpuPoseTarget input = source.Input;
            int count = input.Skeleton.Skeleton.BoneCount, blends = input.Blend?.Steps.Length ?? 0;
            bones += count; states += blends; locals += count * blends;
            controls += input.Controls?.Length ?? 0; goals += input.Rig?.GoalCount ?? 0;
            operations += (input.Rig?.Steps.Length ?? 0) + (input.Rig?.TranslationBone >= 0 ? 1 : 0);
            if (input.Rig is { } rig)
                foreach (GpuPoseSource step in rig.Steps)
                    if (step is GpuXpbdPose chain)
                    { bodies += chain.Chain.Bones.Length; lengths += chain.Chain.Bones.Length - 1; bends += chain.Chain.Bones.Length - 2; }
        }
        var represented = _inputs.Select(source => source.Input.Skeleton).ToHashSet();
        foreach (var source in _inputs)
            foreach (SkeletonInstance section in source.Input.Skeleton.Sections)
                if (represented.Add(section)) bones += section.Skeleton.BoneCount;
        return (new(bodies, Math.Max(1, lengths), Math.Max(1, bends)),
            new(_inputs.Count, bones, Math.Max(1, states), Math.Max(1, locals), Math.Max(1, controls), 0, Math.Max(1, goals), Math.Max(1, operations)));
    }

    private static bool Grows(DevicePoseCapacity previous, DevicePoseCapacity required) =>
        required.Targets > previous.Targets || required.Bones > previous.Bones || required.BlendStates > previous.BlendStates
        || required.BlendLocals > previous.BlendLocals || required.Controls > previous.Controls || required.Goals > previous.Goals || required.Operations > previous.Operations;
    private static DevicePoseCapacity Grow(DevicePoseCapacity previous, DevicePoseCapacity required) => new(
        Math.Max(required.Targets, previous.Targets * 2), Math.Max(required.Bones, previous.Bones * 2),
        Math.Max(required.BlendStates, previous.BlendStates * 2), Math.Max(required.BlendLocals, previous.BlendLocals * 2),
        Math.Max(required.Controls, previous.Controls * 2), 0, Math.Max(required.Goals, previous.Goals * 2), Math.Max(required.Operations, previous.Operations * 2));

    private static bool HasChain(GpuPoseRig? rig)
    {
        if (rig is null) return false;
        foreach (GpuPoseSource step in rig.Steps) if (step is GpuXpbdPose) return true;
        return false;
    }

    public void OnDestroy(ref ImmediateSystemContext context)
    {
        _completion.Complete(); _sources?.Dispose(); _physics?.Dispose();
        foreach (var retired in _retired) { retired.Sources.Dispose(); retired.Physics.Dispose(); }
        context.World.ReleaseQuery(_targets);
    }
}
