using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics;

/// <summary>Reconstructs velocities after the project's position and rotation solve stages.</summary>
public readonly struct UpdateBodyVelocities(int rowsPerPacket) : IPhysicsStage
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) =>
        new UpdateBodyVelocityJob((float)context.Duration).ScheduleParallel(context.World,
            new JobEntityScheduleOptions(rowsPerPacket: rowsPerPacket, filter: context.Bodies), dependency);
}

internal readonly partial struct UpdateBodyVelocityJob(float duration) : IJobEntity
{
    public void Execute(in BodyPose pose, ref BodyVelocity velocity, in BodyMass mass, in BodyStepState state)
    {
        if (mass.Motion == BodyMotion.Static) return;
        velocity.Linear = (pose.GetCenterOfMass(in mass) - state.StartCenterOfMass) / duration;
        velocity.Angular = BodyRotation.Velocity(state.StartRotation, pose.Rotation, duration);
    }
}
