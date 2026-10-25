using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;

namespace SomeEngine.Physics;

public readonly struct XpbdJointMotorProjection : IXpbdConstraint<JointConstraint>
{
    public static void Initialize(in JointConstraint constraint, scoped QueryRow row, in PhysicsStepContext context) =>
        row.ReadWrite<XpbdMotorState>() = default;

    public static void Project(in JointConstraint constraint, scoped QueryRow row,
        scoped PhysicsBodies bodies, in PhysicsStepContext context)
    {
        ref readonly JointMotor motor = ref row.ReadRef<JointMotor>();
        ref readonly var endpoints = ref row.ReadRef<DirectedRelationEndpoints<JointConstraint>>();
        ref readonly BodyMass mass = ref bodies.ReadMass(endpoints.Target);
        ref readonly BodyMass sourceMass = ref bodies.ReadMass(endpoints.Source);
        bool react = motor.Response == JointMotorResponse.Both;
        if (mass.Motion != BodyMotion.Dynamic && !(react && sourceMass.Motion == BodyMotion.Dynamic)) return;
        ref XpbdMotorState state = ref row.ReadWrite<XpbdMotorState>();
        float duration = (float)context.Duration;
        for (int dof = 0; dof < 6; dof++)
        {
            int axis = dof % 3;
            if (dof < 3 ? motor.LinearStiffness[axis] == 0 && motor.LinearDamping[axis] == 0
                : motor.AngularStiffness[axis] == 0 && motor.AngularDamping[axis] == 0) continue;
            JointMotorRow motorRow = JointMotorMath.GetRow(in constraint, row, bodies, in context, dof);
            ref readonly BodyPose pose = ref bodies.ReadPose(endpoints.Target);
            float alpha = 1 / (motorRow.Stiffness * duration * duration);
            float weight = BodyConstraintMath.GetWeight(in mass, in pose,
                motorRow.Derivative.TargetLinear, motorRow.Derivative.TargetAngular);
            if (react && motor.Space == JointMotorSpace.Source)
                weight += BodyConstraintMath.GetWeight(in sourceMass, in bodies.ReadPose(endpoints.Source),
                    motorRow.Derivative.SourceLinear, motorRow.Derivative.SourceAngular);
            float multiplier = dof < 3 ? state.LinearMultiplier[axis] : state.AngularMultiplier[axis];
            float change = (-motorRow.Error - alpha * multiplier) / (weight + alpha);
            float maximum = motorRow.Maximum * duration * duration;
            float next = Math.Clamp(multiplier + change, -maximum, maximum);
            change = next - multiplier;
            if (dof < 3) state.LinearMultiplier[axis] = next;
            else state.AngularMultiplier[axis] = next;
            BodyConstraintMath.ApplyCorrection(bodies, endpoints.Target, in mass,
                motorRow.Derivative.TargetLinear, motorRow.Derivative.TargetAngular, change);
            if (react)
                BodyConstraintMath.ApplyCorrection(bodies, endpoints.Source, in sourceMass,
                    motorRow.Derivative.SourceLinear, motorRow.Derivative.SourceAngular, change);
        }
    }
}
