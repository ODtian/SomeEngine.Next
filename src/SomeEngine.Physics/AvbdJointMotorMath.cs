using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;

namespace SomeEngine.Physics;

public readonly struct AvbdJointMotorMath : IAvbdConstraint<JointConstraint>
{
    public static int GetReadBodyCount(in JointConstraint constraint, scoped QueryRow row, in PhysicsStepContext context) =>
        row.ReadRef<JointMotor>().Response == JointMotorResponse.Both ? 0 : 1;
    public static Entity GetReadBody(in JointConstraint constraint, scoped QueryRow row, int index, in PhysicsStepContext context) =>
        row.ReadRef<DirectedRelationEndpoints<JointConstraint>>().Source;
    public static int GetBodyCount(in JointConstraint constraint, scoped QueryRow row, in PhysicsStepContext context) =>
        row.ReadRef<JointMotor>().Response == JointMotorResponse.Both ? 2 : 1;

    public static Entity GetBody(in JointConstraint constraint, scoped QueryRow row, int index, in PhysicsStepContext context)
    {
        ref readonly var endpoints = ref row.ReadRef<DirectedRelationEndpoints<JointConstraint>>();
        return row.ReadRef<JointMotor>().Response == JointMotorResponse.Both && index == 0 ? endpoints.Source : endpoints.Target;
    }

    public static void Initialize(in JointConstraint constraint, scoped QueryRow row, scoped PhysicsBodies bodies,
        in PhysicsStepContext context, float retention)
    {
        ref AvbdMotorState state = ref row.ReadWrite<AvbdMotorState>();
        state.LinearMultiplier *= retention;
        state.AngularMultiplier *= retention;
    }

    public static void AccumulateBodyEquation(in JointConstraint constraint, scoped QueryRow row, Entity body,
        scoped PhysicsBodies bodies, in PhysicsStepContext context, float initialErrorFraction, ref AvbdBodyEquation equation)
    {
        ref readonly JointMotor motor = ref row.ReadRef<JointMotor>();
        ref readonly AvbdMotorState state = ref row.ReadRef<AvbdMotorState>();
        bool isSource = body == row.ReadRef<DirectedRelationEndpoints<JointConstraint>>().Source;
        for (int dof = 0; dof < 6; dof++)
        {
            int axis = dof % 3;
            if (dof < 3 ? motor.LinearStiffness[axis] == 0 && motor.LinearDamping[axis] == 0
                : motor.AngularStiffness[axis] == 0 && motor.AngularDamping[axis] == 0) continue;
            JointMotorRow motorRow = JointMotorMath.GetRow(in constraint, row, bodies, in context, dof);
            float penalty = dof < 3 ? state.LinearPenalty[axis] : state.AngularPenalty[axis];
            float multiplier = dof < 3 ? state.LinearMultiplier[axis] : state.AngularMultiplier[axis];
            var material = new AvbdMaterialTerms(motorRow.Stiffness, penalty);
            float force = material.EvaluateForce(motorRow.Error - initialErrorFraction * motorRow.InitialError,
                multiplier);
            float limited = Math.Clamp(force, -motorRow.Maximum, motorRow.Maximum);
            float curvature = MathF.Abs(force) < motorRow.Maximum && !(isSource && motor.Space == JointMotorSpace.World)
                ? material.EffectiveStiffness : 0;
            equation.AddRow(isSource ? motorRow.Derivative.SourceLinear : motorRow.Derivative.TargetLinear,
                isSource ? motorRow.Derivative.SourceAngular : motorRow.Derivative.TargetAngular, limited, curvature);
        }
    }

    public static void UpdateDuals(in JointConstraint constraint, scoped QueryRow row, scoped PhysicsBodies bodies,
        in PhysicsStepContext context, float initialErrorFraction)
    {
        ref readonly JointMotor motor = ref row.ReadRef<JointMotor>();
        ref AvbdMotorState state = ref row.ReadWrite<AvbdMotorState>();
        for (int dof = 0; dof < 6; dof++)
        {
            int axis = dof % 3;
            if (dof < 3 ? motor.LinearStiffness[axis] == 0 && motor.LinearDamping[axis] == 0
                : motor.AngularStiffness[axis] == 0 && motor.AngularDamping[axis] == 0) continue;
            JointMotorRow motorRow = JointMotorMath.GetRow(in constraint, row, bodies, in context, dof);
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
