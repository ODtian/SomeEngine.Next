using System.Numerics;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS;
using SomeEngine.ECS.Relations;

namespace SomeEngine.Physics;

public readonly partial struct AvbdJointMotorMath
{

    public static void GetBodyForce(in JointConstraint constraint, in RelationEndpoints<JointConstraint> endpoints, in JointMotor motor, in JointMotorTarget target, ref AvbdMotorState state, Entity body,
        scoped PhysicsBodies bodies, in PhysicsStepContext context, out Vector3 force, out Vector3 torque)
    {
        bool react = motor.Response == JointMotorResponse.Both;
        force = torque = Vector3.Zero;
        for (int dof = 0; dof < 6; dof++)
        {
            if (!JointMotorMath.IsAxisEnabled(in motor, dof)) continue;
            float multiplier = dof < 3 ? state.LinearMultiplier[dof] : state.AngularMultiplier[dof - 3];
            if (multiplier == 0) continue;
            JointEquationRow derivative = JointMotorMath.GetRow(in constraint, in endpoints, in motor, in target, bodies, in context, dof).Derivative;
            if (body == endpoints.Target) { force -= derivative.TargetLinear * multiplier; torque -= derivative.TargetAngular * multiplier; }
            if (react && body == endpoints.Source) { force -= derivative.SourceLinear * multiplier; torque -= derivative.SourceAngular * multiplier; }
        }
    }

    public static PhysicsConstraintError GetActivityError(in JointConstraint constraint, in RelationEndpoints<JointConstraint> endpoints, in JointMotor motor, in JointMotorTarget target,
        scoped PhysicsBodies bodies, in PhysicsStepContext context) => JointMotorMath.GetActivityError(in constraint, in endpoints, in motor, in target, bodies, in context);







    public static void Initialize(in JointConstraint constraint, in RelationEndpoints<JointConstraint> endpoints, in JointMotor motor, in JointMotorTarget target, ref AvbdMotorState state, scoped PhysicsBodies bodies,
        in PhysicsStepContext context, float retention)
    {
        for (int dof = 0; dof < 6; dof++)
        {
            int axis = dof % 3;
            bool enabled = JointMotorMath.IsAxisEnabled(in motor, dof);
            if (dof < 3) state.LinearMultiplier[axis] = enabled ? state.LinearMultiplier[axis] * retention : 0;
            else state.AngularMultiplier[axis] = enabled ? state.AngularMultiplier[axis] * retention : 0;
        }
    }

    public static void AccumulateBodyEquation(in JointConstraint constraint, in RelationEndpoints<JointConstraint> endpoints, in JointMotor motor, in JointMotorTarget target, ref AvbdMotorState state, Entity body,
        scoped PhysicsBodies bodies, in PhysicsStepContext context, float initialErrorFraction, ref AvbdBodyEquation equation)
    {
        bool isSource = body == endpoints.Source;
        for (int dof = 0; dof < 6; dof++)
        {
            int axis = dof % 3;
            if (!JointMotorMath.IsAxisEnabled(in motor, dof)) continue;
            JointMotorRow motorRow = JointMotorMath.GetRow(in constraint, in endpoints, in motor, in target, bodies, in context, dof);
            float penalty = dof < 3 ? state.LinearPenalty[axis] : state.AngularPenalty[axis];
            float multiplier = dof < 3 ? state.LinearMultiplier[axis] : state.AngularMultiplier[axis];
            var material = new AvbdMaterialTerms(motorRow.Stiffness, penalty);
            float force = material.EvaluateForce(motorRow.Error - initialErrorFraction * motorRow.InitialError,
                multiplier);
            float limited = Math.Clamp(force, -motorRow.Maximum, motorRow.Maximum);
            // The material curvature bounds the whole capped-force potential across its two branches.
            float curvature = isSource && motor.Space == JointMotorSpace.World ? 0 : material.EffectiveStiffness;
            equation.AddRow(isSource ? motorRow.Derivative.SourceLinear : motorRow.Derivative.TargetLinear,
                isSource ? motorRow.Derivative.SourceAngular : motorRow.Derivative.TargetAngular, limited, curvature);
        }
    }

    public static void UpdateDuals(in JointConstraint constraint, in RelationEndpoints<JointConstraint> endpoints, in JointMotor motor, in JointMotorTarget target, ref AvbdMotorState state, scoped PhysicsBodies bodies,
        in PhysicsStepContext context, float initialErrorFraction)
    {
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
            float penalty = dof < 3 ? state.LinearPenalty[axis] : state.AngularPenalty[axis];
            float multiplier = dof < 3 ? state.LinearMultiplier[axis] : state.AngularMultiplier[axis];
            var material = new AvbdMaterialTerms(motorRow.Stiffness, penalty);
            float force = Math.Clamp(material.EvaluateForce(motorRow.Error - initialErrorFraction * motorRow.InitialError,
                multiplier), -motorRow.Maximum, motorRow.Maximum);
            if (dof < 3) state.LinearMultiplier[axis] = force;
            else state.AngularMultiplier[axis] = force;
        }
    }
}
