using SomeEngine.ECS;
using SomeEngine.ECS.Entities;

namespace SomeEngine.Physics;

/// <summary>Current geometric constraint error in metres and radians.</summary>
public readonly record struct PhysicsConstraintError(float Linear, float Angular);

/// <summary>Collects actual bodies whose constraints exceed the selected quiet tolerances.</summary>
public readonly ref struct PhysicsActivityErrors
{
    private readonly BodyActivitySettings _settings;
    private readonly HashSet<Entity> _bodies;
    internal PhysicsActivityErrors(BodyActivitySettings settings, HashSet<Entity> bodies) { _settings = settings; _bodies = bodies; }
    public void Add(Entity body, in PhysicsConstraintError error) { if (Exceeds(in error)) _bodies.Add(body); }
    public void Add<TConstraint, TParticipants>(World world, Entity entity, in TConstraint constraint,
        scoped PhysicsBodies bodies, in PhysicsStepContext context)
        where TConstraint : struct, IComponent where TParticipants : struct, IPhysicsConstraintBodies<TConstraint>
    {
        PhysicsConstraintError error = TParticipants.GetActivityError(in constraint, world, entity, bodies, in context);
        if (!Exceeds(in error)) return;
        for (int index = 0; index < TParticipants.GetBodyCount(in constraint, world, entity, in context); index++)
            _bodies.Add(TParticipants.GetBody(in constraint, world, entity, index, in context));
        for (int index = 0; index < TParticipants.GetReadBodyCount(in constraint, world, entity, in context); index++)
            _bodies.Add(TParticipants.GetReadBody(in constraint, world, entity, index, in context));
    }
    private bool Exceeds(in PhysicsConstraintError error) => error.Linear > _settings.LinearErrorThreshold || error.Angular > _settings.AngularErrorThreshold;
}
