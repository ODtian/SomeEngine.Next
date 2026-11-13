using System.Numerics;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS;
using SomeEngine.ECS.Relations;

namespace SomeEngine.Physics;

public readonly partial struct XpbdJointMotorProjection
{








    public static void GetBodyForce(in JointConstraint constraint, in RelationEndpoints<JointConstraint> endpoints, in JointMotor motor, in JointMotorTarget target, ref XpbdMotorState state, Entity body,
        scoped PhysicsBodies bodies, in PhysicsStepContext context, out Vector3 force, out Vector3 torque)
    {
        bool react = motor.Response == JointMotorResponse.Both;
        float duration = (float)context.Duration;
        force = torque = Vector3.Zero;
        for (int dof = 0; dof < 6; dof++)
        {
            if (!JointMotorMath.IsAxisEnabled(in motor, dof)) continue;
            float multiplier = (dof < 3 ? state.LinearMultiplier[dof] : state.AngularMultiplier[dof - 3]) / (duration * duration);
            if (multiplier == 0) continue;
            JointEquationRow derivative = JointMotorMath.GetRow(in constraint, in endpoints, in motor, in target, bodies, in context, dof).Derivative;
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

    public static void Initialize(in JointConstraint constraint, in RelationEndpoints<JointConstraint> endpoints, in JointMotor motor, in JointMotorTarget target, ref XpbdMotorState state, in PhysicsStepContext context) =>
        state = default;

    public static void Project(in JointConstraint constraint, in RelationEndpoints<JointConstraint> endpoints, in JointMotor motor, in JointMotorTarget target, ref XpbdMotorState state,
        scoped PhysicsBodies bodies, in PhysicsStepContext context)
    {
        ref readonly BodyMass mass = ref bodies.ReadMass(endpoints.Target);
        ref readonly BodyMass sourceMass = ref bodies.ReadMass(endpoints.Source);
        bool react = motor.Response == JointMotorResponse.Both;
        if (mass.Motion != BodyMotion.Dynamic && !(react && sourceMass.Motion == BodyMotion.Dynamic)) return;
        float duration = (float)context.Duration;
        for (int dof = 0; dof < 6; dof++)
        {
            int axis = dof % 3;
            if (!JointMotorMath.IsAxisEnabled(in motor, dof))
            {
                if (dof < 3) state.LinearMultiplier[axis] = 0;
                else state.AngularMultiplier[axis] = 0;
                continue;
            }
            JointMotorRow motorRow = JointMotorMath.GetRow(in constraint, in endpoints, in motor, in target, bodies, in context, dof);
            ref readonly BodyPose pose = ref bodies.ReadPose(endpoints.Target);
            float weight = BodyConstraintMath.GetWeight(in mass, in pose,
                motorRow.Derivative.TargetLinear, motorRow.Derivative.TargetAngular);
            if (react && motor.Space == JointMotorSpace.Source)
                weight += BodyConstraintMath.GetWeight(in sourceMass, in bodies.ReadPose(endpoints.Source),
                    motorRow.Derivative.SourceLinear, motorRow.Derivative.SourceAngular);
            float multiplier = dof < 3 ? state.LinearMultiplier[axis] : state.AngularMultiplier[axis];
            float maximum = motorRow.Maximum * duration * duration;
            var terms = new XpbdMaterialTerms(motorRow.Stiffness, 0, duration);
            float next = Math.Clamp(terms.Project(motorRow.Error, motorRow.Error, weight, multiplier), -maximum, maximum);
            float change = next - multiplier;
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
