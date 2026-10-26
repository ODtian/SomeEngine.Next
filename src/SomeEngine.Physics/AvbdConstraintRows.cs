using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;

namespace SomeEngine.Physics;

internal static class AvbdConstraintRows<TConstraint, TMath>
    where TConstraint : struct, IComponent where TMath : struct, IAvbdConstraint<TConstraint>
{
    internal static void Initialize(scoped PhysicsQueryRows rows, scoped PhysicsBodies bodies,
        in PhysicsStepContext context, float retention, AvbdBodyConstraints byBody)
    {
        foreach (QueryRow row in rows.Rows)
        {
            ref readonly TConstraint constraint = ref row.ReadRef<TConstraint>();
            TMath.Initialize(in constraint, row, bodies, in context, retention);
            for (int index = 0; index < TMath.GetBodyCount(in constraint, row, in context); index++)
                byBody.Add(TMath.GetBody(in constraint, row, index, in context), row.Entity);
        }
    }
    internal static void Accumulate(scoped PhysicsQueryRows rows, Entity body, scoped PhysicsBodies bodies,
        in PhysicsStepContext context, float initialErrorFraction, ReadOnlySpan<Entity> constraints, ref AvbdBodyEquation equation)
    {
        foreach (Entity entity in constraints)
        {
            rows.TryGetRow(entity, out QueryRow row);
            TMath.AccumulateBodyEquation(in row.ReadRef<TConstraint>(), row, body, bodies, in context, initialErrorFraction, ref equation);
        }
    }
    internal static void UpdateDuals(scoped PhysicsQueryRows rows, scoped PhysicsBodies bodies,
        in PhysicsStepContext context, float initialErrorFraction)
    {
        foreach (QueryRow row in rows.Rows)
            TMath.UpdateDuals(in row.ReadRef<TConstraint>(), row, bodies, in context, initialErrorFraction);
    }
}
