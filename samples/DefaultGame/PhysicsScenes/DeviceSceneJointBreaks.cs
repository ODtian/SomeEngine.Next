using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Job;
using SomeEngine.Physics;

namespace DefaultGame;

internal sealed class DeviceSceneJointBreaks : IDisposable
{
    private readonly World _world;
    private readonly Query _joints;
    private readonly DeviceConstraintForceSamples<DeviceJointConstraint> _forces;
    private readonly DeviceConstraintForceLimits<DeviceJointConstraint> _limits;
    private readonly BreakConstraints<JointConstraint, DeviceConstraintForces<JointConstraint, DeviceJointConstraint>> _breaks;

    internal DeviceSceneJointBreaks(IGraphicsBackend backend, Device device, World world, DeviceBodies bodies,
        Query members, ReadOnlySpan<Entity> joints, DeviceSceneJoints inputs, ComputePipelineDesc kernel,
        IReadOnlyDictionary<string, ResourceBinding[]> bindings, PhysicsEventBuffer events, int threads)
    {
        _world = world;
        Scheduler scheduler = world.Execution!.Scheduler!;
        JobResourceAccess[] reads = [.. inputs.Data.GetReadAccesses(scheduler), inputs.States.GetReadAccess(scheduler)];
        _joints = world.Query(new QueryDescription().WithStored<ScenePhysicsJoint>().WithStored<JointConstraint>()
            .WithStored<ConstraintBreakSettings>().WithStored<DeviceConstraintBinding<DeviceJointConstraint>>()
            .WithStored<DeviceConstraintForceBinding<DeviceJointConstraint>>());
        _limits = new(backend, device, world, _joints, inputs.Data);
        _forces = new(backend, device, bodies, inputs.Data, kernel, 2, bindings, reads, threads,
            new IDeviceConstraintSelection[] { inputs.Selection, inputs.StateSelection }, _limits);
        foreach (Entity joint in joints) world.Add(joint, new DeviceConstraintForceBinding<DeviceJointConstraint>(_forces));
        _breaks = new(world, members, _joints, events, _forces.GetReadAccesses(scheduler));
    }

    internal BreakOverloadedDeviceSceneJoints BreakOverloaded() => new(this);

    internal JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency)
    {
        if (_joints.Count == 0) return dependency;
        dependency = _forces.Readback().Schedule(in context, dependency);
        Scheduler scheduler = _world.Execution!.Scheduler!;
        return scheduler.Schedule(new Apply(this, context), _forces.GetReadAccesses(scheduler), dependency);
    }

    private readonly struct Apply(DeviceSceneJointBreaks joints, PhysicsStepContext context) : IJob
    {
        public void Execute()
        {
            if (joints._forces.Count != 0) joints._breaks.Schedule(in context, default);
        }
    }

    public void Dispose()
    {
        _breaks.Dispose();
        _forces.Dispose(); _limits.Dispose();

    }
}

internal readonly struct BreakOverloadedDeviceSceneJoints(DeviceSceneJointBreaks joints) : IPhysicsStage
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => joints.Schedule(in context, dependency);
}
