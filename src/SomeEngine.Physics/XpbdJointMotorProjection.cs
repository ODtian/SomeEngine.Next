using System.Numerics;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;

namespace SomeEngine.Physics;

public readonly struct XpbdJointMotorProjection : IXpbdConstraint<JointConstraint>, IXpbdConstraintForces<JointConstraint>
{
    public static PhysicsConstraintError GetActivityError(in JointConstraint constraint, scoped QueryRow row,
        scoped PhysicsBodies bodies, in PhysicsStepContext context) => JointMotorMath.GetActivityError(in constraint, row, bodies, in context);

    public static int GetReadBodyCount(in JointConstraint constraint, scoped QueryRow row, in PhysicsStepContext context) =>
        row.ReadRef<JointMotor>().Response == JointMotorResponse.Both ? 0 : 1;
    public static Entity GetReadBody(in JointConstraint constraint, scoped QueryRow row, int index, in PhysicsStepContext context) =>
        row.ReadRef<DirectedRelationEndpoints<JointConstraint>>().Source;
    public static int GetBodyCount(in JointConstraint constraint, scoped QueryRow row, in PhysicsStepContext context) =>
        row.ReadRef<JointMotor>().Response == JointMotorResponse.Both ? 2 : 1;

    public static Entity GetBody(in JointConstraint constraint, scoped QueryRow row, int index, in PhysicsStepContext context)
    {
        ref readonly var endpoints = ref row.ReadRef<DirectedRelationEndpoints<JointConstraint>>();
        return index == 0 ? endpoints.Target : endpoints.Source;
    }

    public static void GetBodyForce(in JointConstraint constraint, scoped QueryRow row, Entity body,
        scoped PhysicsBodies bodies, in PhysicsStepContext context, out Vector3 force, out Vector3 torque)
    {
        ref readonly var endpoints = ref row.ReadRef<DirectedRelationEndpoints<JointConstraint>>();
        ref readonly XpbdMotorState state = ref row.ReadRef<XpbdMotorState>();
        bool react = row.ReadRef<JointMotor>().Response == JointMotorResponse.Both;
        float duration = (float)context.Duration;
        force = torque = Vector3.Zero;
        for (int dof = 0; dof < 6; dof++)
        {
            float multiplier = (dof < 3 ? state.LinearMultiplier[dof] : state.AngularMultiplier[dof - 3]) / (duration * duration);
            if (multiplier == 0) continue;
            JointEquationRow derivative = JointMotorMath.GetRow(in constraint, row, bodies, in context, dof).Derivative;
            if (body == endpoints.Target)
            {
                force += derivative.TargetLinear * multiplier;
                torque += derivative.TargetAngular * multiplier;
            }
            if (react && body == endpoints.Source)
            {
                force += derivative.SourceLinear * multiplier;
                torque += derivative.SourceAngular * multiplier;
            }
        }
    }

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
