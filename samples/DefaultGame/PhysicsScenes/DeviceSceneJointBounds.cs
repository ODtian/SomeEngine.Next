using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;
using SomeEngine.Graphics;
using SomeEngine.Job;
using SomeEngine.Physics;

namespace DefaultGame;

internal sealed class DeviceSceneJointBounds : IDisposable
{
    private readonly World _world;
    private readonly QueryHandle _joints;
    private readonly DeviceConstraintData<DeviceJointBoundSet>? _sets;
    private readonly DeviceJointBounds? _bounds;
    private readonly WriteDeviceConstraintInputs<JointConstraint, DeviceJointBoundSet, DeviceJointBoundSource>? _inputs;
    private readonly DeviceConstraintSelection<JointConstraint, DeviceJointBoundSet, DeviceJointBoundsRows>? _selected;

    internal DeviceSceneJointBounds(IGraphicsBackend backend, Device device, World world, QueryHandle bodies,
        ReadOnlySpan<Entity> joints, DeviceConstraintData<DeviceJointConstraint> frames,
        JointLongRangeSettings settings, int boundsPerJoint)
    {
        _world = world;
        if (settings.Levels == 0 || joints.IsEmpty) return;
        _sets = new(backend, device, joints.Length, "DeviceJointBoundSets");
        foreach (Entity joint in joints) world.AddBuffer<LongRangeJointBound>(joint);
        _joints = world.Query(new QueryDefinitionBuilder().All<ScenePhysicsJoint>().Read<JointConstraint>()
            .Read<DirectedRelationEndpoints<JointConstraint>>().WriteBuffer<LongRangeJointBound>());
        _bounds = new(backend, device, world, bodies, _joints, frames, _sets, boundsPerJoint, settings);
        for (int index = 0; index < joints.Length; index++)
        {
            world.Add(joints[index], new DeviceConstraintBinding<DeviceJointBoundSet>(_sets, index));
            world.Add(joints[index], new DeviceJointBoundsBinding(_bounds));
        }
        _inputs = _bounds.WriteInputs(64);
        _selected = new(backend, device, world, _joints, _sets, new(_bounds), "DeviceJointBoundSetsSelection");
        Selections = [_selected];
    }

    internal int Capacity => _bounds?.Capacity ?? 0;
    internal IDeviceConstraintSelection[] Selections { get; } = [];
    internal void AddBindings(Dictionary<string, ResourceBinding[]> bindings)
    {
        if (_sets is null) return;
        _sets.AddBindings(bindings);
        _bounds!.AddBindings(bindings);
    }
    internal JobResourceAccess[] GetReadAccesses(Scheduler scheduler) => _sets is null ? []
        : [.. _sets.GetReadAccesses(scheduler), .. _bounds!.GetReadAccesses(scheduler)];
    internal JobResourceAccess[] GetWriteAccesses(Scheduler scheduler) => _bounds?.GetWriteAccesses(scheduler) ?? [];
    internal BuildDeviceSceneJointBounds Build() => new(this);
    internal UploadDeviceSceneJointBounds Upload() => new(this);
    internal JobHandle ScheduleBuild(in PhysicsStepContext context, JobHandle dependency) => _bounds is null
        ? dependency : _bounds.BuildInputs().Schedule(in context, dependency);
    internal JobHandle ScheduleUpload(in PhysicsStepContext context, JobHandle dependency) => _sets is null
        ? dependency : _sets.BeginInputs().Then(_inputs!.Value).Then(_sets.UploadInputs()).Then(_bounds!.UploadInputs())
            .Schedule(in context, dependency);

    public void Dispose()
    {
        if (_bounds is null) return;
        _selected!.Dispose();
        _inputs!.Value.Dispose();
        _bounds.Dispose();
        _sets!.Dispose();
        _world.ReleaseQuery(_joints);
    }
}

internal readonly struct BuildDeviceSceneJointBounds(DeviceSceneJointBounds bounds) : IPhysicsStage
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => bounds.ScheduleBuild(in context, dependency);
}

internal readonly struct UploadDeviceSceneJointBounds(DeviceSceneJointBounds bounds) : IPhysicsStage
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => bounds.ScheduleUpload(in context, dependency);
}
