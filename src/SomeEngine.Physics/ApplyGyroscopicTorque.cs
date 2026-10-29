// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2025 The Newton Developers
// Newton v1.7.0 angular prediction, through NewtonRigid.cpp:newtonAngularPrediction.
// Source and license: ThirdParty/Newton/NOTICE and the repository LICENSE.
using System.Numerics;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics;

/// <summary>Applies body-space gyroscopic torque before the common pose prediction stage.</summary>
public readonly struct ApplyGyroscopicTorque(int rowsPerPacket) : IPhysicsStage
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) =>
        new ApplyGyroscopicTorqueJob((float)context.Duration).ScheduleParallel(context.World,
            new JobEntityScheduleOptions(rowsPerPacket: rowsPerPacket, filter: context.Bodies), dependency);
}

internal readonly partial struct ApplyGyroscopicTorqueJob(float duration) : IJobEntity
{
    public void Execute(in BodyPose pose, in BodyMass mass, ref BodyVelocity velocity)
    {
        if (mass.Motion != BodyMotion.Dynamic || !BodyInertia.HasRotation(in mass.InverseInertia)) return;
        Vector3 angularVelocity = BodyRotation.TransformVector(velocity.Angular, Quaternion.Conjugate(pose.Rotation));
        Matrix4x4 inertia = BodyInertia.InvertTensor(mass.InverseInertia);
        Vector3 torque = -Vector3.Cross(angularVelocity, Vector3.TransformNormal(angularVelocity, inertia));
        angularVelocity += Vector3.TransformNormal(torque, mass.InverseInertia) * duration;
        velocity.Angular = BodyRotation.TransformVector(angularVelocity, pose.Rotation);
    }
}
