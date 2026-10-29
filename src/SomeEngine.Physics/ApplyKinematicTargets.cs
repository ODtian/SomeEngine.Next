using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics;

/// <summary>Samples explicit kinematic pose trajectories after the common prediction stage.</summary>
public readonly struct ApplyKinematicTargets(int rowsPerPacket) : IPhysicsStage
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) =>
        new ApplyKinematicTargetsJob(context.EndTime).ScheduleParallel(context.World,
            new JobEntityScheduleOptions(rowsPerPacket: rowsPerPacket, filter: context.Bodies), dependency);
}

internal readonly partial struct ApplyKinematicTargetsJob(double time) : IJobEntity
{
    public void Execute(ref BodyPose pose, in BodyMass mass, ref BodyStepState state, BufferView<BodyKinematicTarget> targets)
    {
        if (mass.Motion != BodyMotion.Kinematic) return;
        double latestStart = double.NegativeInfinity;
        foreach (ref readonly BodyKinematicTarget target in targets.AsSpan())
        {
            if (target.StartTime > time || target.StartTime < latestStart) continue;
            float amount = (float)Math.Clamp((time - target.StartTime) / (target.EndTime - target.StartTime), 0, 1);
            pose.Position = Vector3.Lerp(target.StartPose.Position, target.EndPose.Position, amount);
            pose.Rotation = Quaternion.Slerp(target.StartPose.Rotation, target.EndPose.Rotation, amount);
            latestStart = target.StartTime;
        }
        state.PredictedCenterOfMass = pose.GetCenterOfMass(in mass);
        state.PredictedRotation = pose.Rotation;
    }
}
