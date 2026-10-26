using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;

namespace SomeEngine.Physics;

/// <summary>Current geometric constraint error in metres and radians.</summary>
public readonly record struct PhysicsConstraintError(float Linear, float Angular);

/// <summary>Marks the actual participating bodies whose constraints exceed the selected quiet tolerances.</summary>
public readonly ref struct PhysicsActivityErrors
{
    private readonly BodyActivitySettings _settings;
    private readonly HashSet<Entity> _bodies;
    internal PhysicsActivityErrors(BodyActivitySettings settings, HashSet<Entity> bodies) { _settings = settings; _bodies = bodies; }
    public void Add(Entity body, in PhysicsConstraintError error)
    {
        if (Exceeds(in error)) _bodies.Add(body);
    }
    public void Add<TConstraint, TParticipants>(scoped QueryRow row, scoped PhysicsBodies bodies, in PhysicsStepContext context)
        where TConstraint : struct, IComponent where TParticipants : struct, IPhysicsConstraintBodies<TConstraint>
    {
        ref readonly TConstraint constraint = ref row.ReadRef<TConstraint>();
        PhysicsConstraintError error = TParticipants.GetActivityError(in constraint, row, bodies, in context);
        if (!Exceeds(in error)) return;
        for (int index = 0; index < TParticipants.GetBodyCount(in constraint, row, in context); index++)
            _bodies.Add(TParticipants.GetBody(in constraint, row, index, in context));
        for (int index = 0; index < TParticipants.GetReadBodyCount(in constraint, row, in context); index++)
            _bodies.Add(TParticipants.GetReadBody(in constraint, row, index, in context));
    }
    private bool Exceeds(in PhysicsConstraintError error) => error.Linear > _settings.LinearErrorThreshold || error.Angular > _settings.AngularErrorThreshold;
}

internal static class PhysicsActivityRows<TConstraint, TParticipants>
    where TConstraint : struct, IComponent where TParticipants : struct, IPhysicsConstraintBodies<TConstraint>
{
    internal static void Read(scoped PhysicsQueryRows rows, scoped PhysicsBodies bodies, in PhysicsStepContext context, ref PhysicsActivityErrors errors)
    {
        foreach (QueryRow row in rows.Rows) errors.Add<TConstraint, TParticipants>(row, bodies, in context);
    }
}
