using System.Numerics;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;

namespace SomeEngine.Physics;

internal readonly record struct JointMotorRow(JointEquationRow Derivative, float Error,
    float InitialError, float Stiffness, float Maximum);

internal static class JointMotorMath
{
    internal static JointMotorRow GetRow(in JointConstraint constraint, scoped QueryRow row,
        scoped PhysicsBodies bodies, in PhysicsStepContext context, int dof)
    {
        ref readonly JointMotor motor = ref row.ReadRef<JointMotor>();
        ref readonly JointMotorTarget target = ref row.ReadRef<JointMotorTarget>();
        ref readonly var endpoints = ref row.ReadRef<DirectedRelationEndpoints<JointConstraint>>();
        ref readonly BodyPose sourcePose = ref bodies.ReadPose(endpoints.Source);
        ref readonly BodyPose targetPose = ref bodies.ReadPose(endpoints.Target);
        ref readonly BodyMass sourceMass = ref bodies.ReadMass(endpoints.Source);
        ref readonly BodyMass targetMass = ref bodies.ReadMass(endpoints.Target);
        BodyPose sourceStart = GetStartPose(bodies.ReadStepState(endpoints.Source), in sourceMass);
        BodyPose targetStart = GetStartPose(bodies.ReadStepState(endpoints.Target), in targetMass);
        int axis = dof % 3;
        Vector3 direction = axis == 0 ? Vector3.UnitX : axis == 1 ? Vector3.UnitY : Vector3.UnitZ;
        JointEquationRow derivative;
        Vector3 start;
        if (motor.Space == JointMotorSpace.Source)
        {
            JointMath.GetCoordinates(in constraint, in sourceStart, in targetStart, out Vector3 linear, out Vector3 angular);
            start = dof < 3 ? linear : angular;
            derivative = JointMath.GetRow(in constraint, in sourcePose, in sourceMass, in targetPose, in targetMass, dof);
        }
        else if (dof < 3)
        {
            Vector3 point = targetPose.Position + Vector3.Transform(constraint.TargetFrame.Position, targetPose.Rotation);
            start = targetStart.Position + Vector3.Transform(constraint.TargetFrame.Position, targetStart.Rotation);
            derivative = new(point[axis], -direction,
                -Vector3.Cross(point - sourcePose.GetCenterOfMass(in sourceMass), direction), direction,
                Vector3.Cross(point - targetPose.GetCenterOfMass(in targetMass), direction));
        }
        else
        {
            Quaternion rotation = targetPose.Rotation * constraint.TargetFrame.Rotation;
            Vector3 angular = BodyRotation.GetRotationVector(rotation);
            Vector3 jacobian = BodyRotation.GetConstraintJacobian(Quaternion.Identity, rotation, angular, direction);
            start = BodyRotation.GetRotationVector(targetStart.Rotation * constraint.TargetFrame.Rotation);
            derivative = new(angular[axis], Vector3.Zero, -jacobian, Vector3.Zero, jacobian);
        }
        float duration = (float)context.Duration;
        float stiffness = dof < 3 ? motor.LinearStiffness[axis] : motor.AngularStiffness[axis];
        float damping = (dof < 3 ? motor.LinearDamping[axis] : motor.AngularDamping[axis]) / duration;
        float combined = stiffness + damping;
        float elapsed = (float)(context.EndTime - target.Time);
        float position;
        float speed;
        if (dof < 3)
        {
            position = target.Frame.Position[axis] + target.LinearVelocity[axis] * elapsed;
            speed = target.LinearVelocity[axis];
        }
        else
        {
            Quaternion rotation = BodyRotation.Integrate(target.Frame.Rotation, target.AngularVelocity, elapsed);
            Vector3 angular = BodyRotation.GetRotationVector(rotation);
            Vector3 jacobian = BodyRotation.GetConstraintJacobian(Quaternion.Identity, rotation, angular, direction);
            position = angular[axis];
            speed = Vector3.Dot(jacobian, target.AngularVelocity);
        }
        float equilibrium = (stiffness * position + damping * (start[axis] + speed * duration)) / combined;
        return new(derivative, derivative.Coordinate - equilibrium, start[axis] - equilibrium,
            combined, dof < 3 ? motor.MaximumForce[axis] : motor.MaximumTorque[axis]);
    }

    private static BodyPose GetStartPose(in BodyStepState state, in BodyMass mass)
    {
        var pose = new BodyPose { Rotation = state.StartRotation };
        pose.SetCenterOfMass(state.StartCenterOfMass, in mass);
        return pose;
    }
}
