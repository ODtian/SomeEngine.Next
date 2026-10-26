using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;

namespace SomeEngine.Physics;

/// <summary>A concrete constraint's affected bodies and additional read dependencies.</summary>
public interface IPhysicsConstraintBodies<TConstraint> where TConstraint : struct, IComponent
{
    static abstract int GetBodyCount(in TConstraint constraint, scoped QueryRow row, in PhysicsStepContext context);
    static abstract Entity GetBody(in TConstraint constraint, scoped QueryRow row, int index, in PhysicsStepContext context);
    static virtual int GetReadBodyCount(in TConstraint constraint, scoped QueryRow row, in PhysicsStepContext context) => 0;
    static virtual Entity GetReadBody(in TConstraint constraint, scoped QueryRow row, int index, in PhysicsStepContext context) => Entity.Null;
    static virtual int GetReadEntityCount(in TConstraint constraint, scoped QueryRow row, in PhysicsStepContext context) => 0;
    static virtual Entity GetReadEntity(in TConstraint constraint, scoped QueryRow row, int index, in PhysicsStepContext context) => Entity.Null;
    static virtual PhysicsConstraintError GetActivityError(in TConstraint constraint, scoped QueryRow row,
        scoped PhysicsBodies bodies, in PhysicsStepContext context) => default;
}

/// <summary>Current world force in N and torque in N·m on one actual participating body.</summary>
public interface IPhysicsConstraintForces<TConstraint> : IPhysicsConstraintBodies<TConstraint> where TConstraint : struct, IComponent
{
    static abstract void GetBodyForce(in TConstraint constraint, scoped QueryRow row, Entity body,
        scoped PhysicsBodies bodies, in PhysicsStepContext context, out Vector3 force, out Vector3 torque);
}
