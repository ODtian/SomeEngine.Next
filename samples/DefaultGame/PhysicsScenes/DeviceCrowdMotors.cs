using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.Graphics;
using SomeEngine.Job;
using SomeEngine.Physics;

namespace DefaultGame;

internal sealed class DeviceCrowdMotors : IDisposable
{
    private readonly World _world;
    private readonly QueryHandle _motors, _states;
    private readonly WriteDeviceConstraintInputs<JointMotor, DeviceMotorConstraint, DeviceMotorSource> _inputs;
    private readonly DeviceAvbdStateInputs<AvbdMotorState, DeviceMotorConstraint, DeviceAvbdMotorState, DeviceAvbdMotorStateSource> _stateInputs;
    internal DeviceCrowdMotors(IGraphicsBackend backend, Device device, World world, ReadOnlySpan<Entity> motors)
    {
        _world = world;
        Data = new(backend, device, motors.Length, "DeviceMotors");
        States = new(backend, device, motors.Length, "DeviceAvbdMotorStates");
        for (int index = 0; index < motors.Length; index++)
        {
            world.Add(motors[index], new DeviceConstraintBinding<DeviceMotorConstraint>(Data, index));
            world.Add(motors[index], new DeviceAvbdState<DeviceAvbdMotorState>(States));
        }
        _motors = world.Query(new QueryDefinitionBuilder().All<CrowdMotorTarget>().Read<JointMotor>().Read<JointMotorTarget>());
        _states = world.Query(new QueryDefinitionBuilder().All<CrowdMotorTarget>().Read<AvbdMotorState>()
            .Read<DeviceConstraintBinding<DeviceMotorConstraint>>().Read<DeviceAvbdState<DeviceAvbdMotorState>>());
        _inputs = Data.WriteInputs<JointMotor, DeviceMotorSource>(world, _motors, default, 64);
        _stateInputs = new(backend, device, world, _states, Data, States, default);
        Selection = new(backend, device, world, _motors, Data, "DeviceMotorsSelection");
        StateSelection = new(backend, device, world, _motors, Data, new(States), "DeviceMotorAvbdSelection");
    }
    internal DeviceConstraintData<DeviceMotorConstraint> Data { get; }
    internal DeviceConstraintState<DeviceAvbdMotorState> States { get; }
    internal DeviceConstraintSelection<JointMotor, DeviceMotorConstraint> Selection { get; }
    internal DeviceConstraintSelection<JointMotor, DeviceMotorConstraint, DeviceAvbdStateRows<DeviceAvbdMotorState>> StateSelection { get; }
    internal void AddBindings(Dictionary<string, ResourceBinding[]> bindings) { Data.AddBindings(bindings); States.AddBindings(bindings); }
    internal UploadCrowdMotors Upload() => new(this);
    internal JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency)
    {
        if (context.Number < 0)
            dependency = Data.UploadInputs().Schedule(in context,
                _inputs.Schedule(in context, Data.BeginInputs().Schedule(in context, dependency)));
        return _stateInputs.Upload().Schedule(in context, dependency);
    }
    public void Dispose()
    {
        StateSelection.Dispose(); Selection.Dispose(); _stateInputs.Dispose(); _inputs.Dispose(); States.Dispose(); Data.Dispose();
        _world.ReleaseQuery(_states); _world.ReleaseQuery(_motors);
    }
}
internal readonly struct UploadCrowdMotors(DeviceCrowdMotors motors) : IPhysicsStage
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => motors.Schedule(in context, dependency);
}
