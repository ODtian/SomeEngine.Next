using SomeEngine.ECS;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics;

/// <summary>Reads the selected bodies' timed input buffers before the prediction stage.</summary>
public readonly struct ApplyBodyInputs(int rowsPerPacket) : IPhysicsStage
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency)
    {
        var options = new JobEntityScheduleOptions(rowsPerPacket: rowsPerPacket, filter: context.Bodies);
        dependency = new ApplyBodyForcesJob(context.StartTime, context.EndTime)
            .ScheduleParallel(context.World, options, dependency);
        dependency = new ApplyBodyImpulsesJob(context.StartTime, context.EndTime)
            .ScheduleParallel(context.World, options, dependency);
        return new ApplyBodyVelocityTargetsJob(context.StartTime, context.EndTime)
            .ScheduleParallel(context.World, options, dependency);
    }
}

internal readonly partial struct ApplyBodyForcesJob(double startTime, double endTime) : IJobEntity
{
    public void Execute(ref BodyVelocity velocity, in BodyPose pose, in BodyMass mass, BufferView<BodyForce> forces)
    {
        if (mass.Motion != BodyMotion.Dynamic) return;
        foreach (ref readonly BodyForce force in forces.AsSpan())
        {
            float duration = (float)Math.Max(0, Math.Min(endTime, force.EndTime) - Math.Max(startTime, force.StartTime));
            velocity.Linear += force.Force * (mass.InverseMass * duration);
            velocity.Angular += mass.ApplyInverseInertia(pose.Rotation, force.Torque) * duration;
        }
    }
}

internal readonly partial struct ApplyBodyImpulsesJob(double startTime, double endTime) : IJobEntity
{
    public void Execute(ref BodyVelocity velocity, in BodyPose pose, in BodyMass mass, BufferView<BodyImpulse> impulses)
    {
        if (mass.Motion != BodyMotion.Dynamic) return;
        foreach (ref readonly BodyImpulse impulse in impulses.AsSpan())
        {
            if (impulse.Time < startTime || impulse.Time >= endTime) continue;
            velocity.Linear += impulse.Linear * mass.InverseMass;
            velocity.Angular += mass.ApplyInverseInertia(pose.Rotation, impulse.Angular);
        }
    }
}

internal readonly partial struct ApplyBodyVelocityTargetsJob(double startTime, double endTime) : IJobEntity
{
    public void Execute(ref BodyVelocity velocity, in BodyMass mass, BufferView<BodyVelocityTarget> targets)
    {
        if (mass.Motion == BodyMotion.Static) return;
        double latestTime = startTime;
        foreach (ref readonly BodyVelocityTarget target in targets.AsSpan())
        {
            if (target.Time < latestTime || target.Time >= endTime) continue;
            velocity.Linear = target.Linear;
            velocity.Angular = target.Angular;
            latestTime = target.Time;
        }
    }
}
