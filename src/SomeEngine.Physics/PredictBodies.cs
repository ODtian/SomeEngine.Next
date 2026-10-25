using System.Numerics;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics;

/// <summary>Integrates velocities and predicts center-of-mass positions and rotations.</summary>
public readonly struct PredictBodies(Vector3 acceleration, int rowsPerPacket) : IPhysicsStage
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) =>
        new PredictBodyJob((float)context.Duration, acceleration, context.Number, context.Substep, context.EndTime)
            .ScheduleParallel(context.World, new JobEntityScheduleOptions(rowsPerPacket: rowsPerPacket,
                filter: context.Bodies), dependency);
}

internal readonly partial struct PredictBodyJob(float duration, Vector3 acceleration, long number,
    int substep, double endTime) : IJobEntity
{
    public void Execute(ref BodyPose pose, ref BodyVelocity velocity, in BodyMass mass, ref BodyStepState state)
    {
        state.StartCenterOfMass = pose.GetCenterOfMass(in mass);
        state.StartRotation = pose.Rotation;
        state.Number = number;
        state.Substep = substep;
        state.Time = endTime;

        if (mass.Motion == BodyMotion.Dynamic)
            velocity.Linear += acceleration * duration;
        if (mass.Motion != BodyMotion.Static)
        {
            pose.Rotation = BodyRotation.Integrate(pose.Rotation, velocity.Angular, duration);
            pose.SetCenterOfMass(state.StartCenterOfMass + velocity.Linear * duration, in mass);
        }

        state.PredictedCenterOfMass = pose.GetCenterOfMass(in mass);
        state.PredictedRotation = pose.Rotation;
    }
}

internal static class BodyRotation
{
    // Newton v1.7.0 joint rotation derivatives; copyright (c) 2025 The Newton Developers.
    // Source and license: ThirdParty/Newton/NOTICE and the repository LICENSE.
    internal static Vector3 GetRotationVector(Quaternion rotation)
    {
        if (rotation.W < 0) rotation = -rotation;
        var vector = new Vector3(rotation.X, rotation.Y, rotation.Z);
        float magnitude = vector.Length();
        return magnitude == 0 ? Vector3.Zero : vector * (2 * MathF.Atan2(magnitude, rotation.W) / magnitude);
    }

    internal static Vector3 GetConstraintJacobian(Quaternion sourceFrame, Quaternion relative,
        Vector3 error, Vector3 direction)
    {
        Vector3 right = direction - Vector3.Cross(error, direction) * 0.5f
            + Vector3.Cross(error, Vector3.Cross(error, direction)) * (1f / 12);
        return Vector3.Transform(Vector3.Transform(right, relative), sourceFrame);
    }

    internal static Quaternion ApplyChange(Quaternion rotation, Vector3 change) =>
        Quaternion.Normalize(rotation + (new Quaternion(change, 0) * rotation) * 0.5f);

    internal static Quaternion Integrate(Quaternion rotation, Vector3 angularVelocity, float duration)
    {
        float speed = angularVelocity.Length();
        if (speed == 0) return rotation;
        Quaternion increment = Quaternion.CreateFromAxisAngle(angularVelocity / speed, speed * duration);
        return Quaternion.Normalize(increment * rotation);
    }

    internal static Vector3 Velocity(Quaternion start, Quaternion end, float duration)
    {
        Quaternion change = Quaternion.Normalize(end * Quaternion.Conjugate(start));
        if (change.W < 0) change = -change;
        var vector = new Vector3(change.X, change.Y, change.Z);
        float sine = vector.Length();
        return sine < 1e-8f ? vector * (2 / duration) :
            vector * (2 * MathF.Atan2(sine, change.W) / (sine * duration));
    }
}
