using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;
using SomeEngine.Graphics;
using SomeEngine.Job;
using SomeEngine.Physics;

namespace DefaultGame;

internal sealed class DeviceSceneJoints : IDisposable
{
    private readonly World _world;
    private readonly QueryHandle _joints, _states;
    private readonly WriteDeviceConstraintInputs<JointConstraint, DeviceJointConstraint, DeviceJointSource> _inputs;
    private readonly DeviceAvbdStateInputs<AvbdJointState, DeviceJointConstraint, DeviceAvbdJointState, DeviceAvbdJointStateSource> _stateInputs;

    internal DeviceSceneJoints(IGraphicsBackend backend, Device device, World world, ReadOnlySpan<Entity> joints)
    {
        _world = world;
        int capacity = Math.Max(1, joints.Length);
        Data = new(backend, device, capacity, "DeviceJoints");
        States = new(backend, device, capacity, "DeviceAvbdJointStates");
        for (int index = 0; index < joints.Length; index++)
        {
            world.Add(joints[index], new DeviceConstraintBinding<DeviceJointConstraint>(Data, index));
            world.Add(joints[index], new DeviceAvbdState<DeviceAvbdJointState>(States));
        }
        _joints = world.Query(new QueryDefinitionBuilder().All<ScenePhysicsJoint>().Read<JointConstraint>()
            .Read<DirectedRelationEndpoints<JointConstraint>>().Read<DeviceConstraintBinding<DeviceJointConstraint>>());
        _states = world.Query(new QueryDefinitionBuilder().All<ScenePhysicsJoint>().Read<AvbdJointState>()
            .Read<DeviceConstraintBinding<DeviceJointConstraint>>().Read<DeviceAvbdState<DeviceAvbdJointState>>());
        _inputs = Data.WriteInputs<JointConstraint, DeviceJointSource>(world, _joints, default, 64);
        _stateInputs = new(backend, device, world, _states, Data, States, default);
        Selection = new(backend, device, world, _joints, Data, "DeviceJointsSelection");
        StateSelection = new(backend, device, world, _joints, Data, new(States), "DeviceJointAvbdSelection");
    }

    internal DeviceConstraintData<DeviceJointConstraint> Data { get; }
    internal DeviceConstraintState<DeviceAvbdJointState> States { get; }
    internal DeviceConstraintSelection<JointConstraint, DeviceJointConstraint> Selection { get; }
    internal DeviceConstraintSelection<JointConstraint, DeviceJointConstraint, DeviceAvbdStateRows<DeviceAvbdJointState>> StateSelection { get; }
    internal UploadDeviceSceneJoints Upload() => new(this);

    internal void AddBindings(Dictionary<string, ResourceBinding[]> bindings)
    {
        Data.AddBindings(bindings);
        States.AddBindings(bindings);
    }

    internal JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency)
    {
        dependency = Data.BeginInputs().Schedule(in context, dependency);
        dependency = _inputs.Schedule(in context, dependency);
        dependency = Data.UploadInputs().Schedule(in context, dependency);
        return _stateInputs.Upload().Schedule(in context, dependency);
    }

    public void Dispose()
    {
        StateSelection.Dispose(); Selection.Dispose(); _stateInputs.Dispose(); _inputs.Dispose();
        States.Dispose(); Data.Dispose();
        _world.ReleaseQuery(_states); _world.ReleaseQuery(_joints);
    }
}

internal readonly struct UploadDeviceSceneJoints(DeviceSceneJoints joints) : IPhysicsStage
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => joints.Schedule(in context, dependency);
}
