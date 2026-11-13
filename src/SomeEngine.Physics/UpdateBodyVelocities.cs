using System.Numerics;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics;

/// <summary>Reconstructs velocities after the project's position and rotation solve stages.</summary>
public readonly struct UpdateBodyVelocities(int rowsPerPacket) : IPhysicsStage
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) =>
        new SystemRunner(context.World, rowsPerPacket).Schedule(context.Bodies,
            new UpdateBodyVelocityJob((float)context.Duration), dependency, parallel: true);
}

internal readonly partial struct UpdateBodyVelocityJob(float duration) : IJobEntity
{
    public void Execute(in BodyPose pose, ref BodyVelocity velocity, in BodyMass mass, in BodyStepState state)
    {
        if (mass.Motion == BodyMotion.Static) return;
        Vector3 center = pose.GetCenterOfMass(in mass);
        velocity.Linear += (center - state.PredictedCenterOfMass) / duration;
        velocity.Angular += BodyRotation.Velocity(state.PredictedRotation, pose.Rotation, duration);
    }
}
