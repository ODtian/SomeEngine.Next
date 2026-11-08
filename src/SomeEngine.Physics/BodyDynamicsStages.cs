using System.Numerics;
using SomeEngine.Job;

namespace SomeEngine.Physics.Commands;

public readonly struct PredictBodies(BodyDynamicsCommands dynamics, Vector3 acceleration) : IDevicePhysicsStage
{
    public object InputKey => dynamics;
    public bool CanBatch => true;
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => dynamics.Schedule(in context, acceleration, 0, dependency);
}
public readonly struct UpdateBodySelection(BodyDynamicsCommands dynamics) : IPhysicsStage
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => dynamics.ScheduleSelection(in context, dependency);
}
public readonly struct UpdateBodyVelocities(BodyDynamicsCommands dynamics) : IDevicePhysicsStage
{
    public object InputKey => dynamics;
    public bool CanBatch => true;
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => dynamics.Schedule(in context, Vector3.Zero, 1, dependency);
}
public readonly struct ApplyGyroscopicTorque(BodyDynamicsCommands dynamics) : IDevicePhysicsStage
{
    public object InputKey => dynamics;
    public bool CanBatch => true;
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => dynamics.ScheduleGyroscopicTorque(in context, dependency);
}
public readonly struct ResumeBodies(BodyDynamicsCommands dynamics, Vector3 acceleration) : IDevicePhysicsStage
{
    public object InputKey => dynamics;
    public bool CanBatch => true;
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => dynamics.ScheduleResume(in context, acceleration, dependency);
}
