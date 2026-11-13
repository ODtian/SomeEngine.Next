using System.Runtime.InteropServices;
using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.Job;

namespace SomeEngine.Physics;

/// <summary>A concrete kernel binding its typed constraint fields to numerical equations.</summary>
public interface IAvbdConstraint<TConstraint> : IPhysicsConstraintBodies<TConstraint> where TConstraint : struct, IComponent
{
    static virtual void AddInputAccesses(World world, Query members, List<JobResourceAccess> accesses) { }
    static abstract void AddAccesses(World world, Query members, List<JobResourceAccess> accesses);
    void Bind(World world, Query members);
    ref readonly TConstraint ReadConstraint(Entity entity);
    void Initialize(in TConstraint constraint, World world, Entity entity, scoped PhysicsBodies bodies,
        in PhysicsStepContext context, float retention);
    void AccumulateBodyEquation(in TConstraint constraint, World world, Entity entity, Entity body,
        scoped PhysicsBodies bodies, in PhysicsStepContext context, float initialErrorFraction, ref AvbdBodyEquation equation);
    void UpdateDuals(in TConstraint constraint, World world, Entity entity, scoped PhysicsBodies bodies,
        in PhysicsStepContext context, float initialErrorFraction);
}

public interface IAvbdConstraints : IAvbdBodyConstraints, IPhysicsConstraintAccess
{
    void Bind(in PhysicsStepContext context) { }
    void Initialize(in PhysicsStepContext context, scoped PhysicsBodies bodies, float retention);
    void UpdateDuals(in PhysicsStepContext context, scoped PhysicsBodies bodies, float initialErrorFraction);
}

/// <summary>One native constraint query and its derived body-to-constraint identities.</summary>
public readonly partial struct AvbdConstraints<TConstraint, TMath>(Query constraints) : IAvbdConstraints
    where TConstraint : struct, IComponent where TMath : struct, IAvbdConstraint<TConstraint>
{
    private readonly AvbdBodyConstraints _byBody = new();
    private readonly TMath _math = new();
    public void Bind(in PhysicsStepContext context) => _math.Bind(context.World, constraints);
    public void AddInputAccesses(World world, List<JobResourceAccess> accesses) => TMath.AddInputAccesses(world, constraints, accesses);
    public void AddAccesses(World world, List<JobResourceAccess> accesses)
    {
        TMath.AddAccesses(world, constraints, accesses);
        accesses.Add(JobResourceAccess.Write((world.Execution!.Scheduler!).GetResourceToken(_byBody)));
    }
    public void Initialize(in PhysicsStepContext context, scoped PhysicsBodies bodies, float retention)
    {
        _byBody.Clear();
        using var scope = constraints.OpenRead();
        foreach (QueryChunk chunk in scope)
        {
            ReadOnlySpan<TConstraint> values = chunk.Read<TConstraint>();
            for (int row = 0; row < chunk.Count; row++)
            {
                Entity entity = chunk.Entities[row];
                _math.Initialize(in values[row], context.World, entity, bodies, in context, retention);
                int count = TMath.GetBodyCount(in values[row], context.World, entity, in context);
                for (int index = 0; index < count; index++)
                    _byBody.Add(TMath.GetBody(in values[row], context.World, entity, index, in context), entity);
            }
        }
    }
    public void AccumulateBodyEquation(in PhysicsStepContext context, Entity body, scoped PhysicsBodies bodies,
        float initialErrorFraction, ref AvbdBodyEquation equation)
    {
        foreach (Entity entity in _byBody.Read(body))
            _math.AccumulateBodyEquation(in _math.ReadConstraint(entity), context.World, entity, body, bodies, in context,
                initialErrorFraction, ref equation);
    }
    public void UpdateDuals(in PhysicsStepContext context, scoped PhysicsBodies bodies, float initialErrorFraction)
    {
        using var scope = constraints.OpenRead();
        foreach (QueryChunk chunk in scope)
        {
            ReadOnlySpan<TConstraint> values = chunk.Read<TConstraint>();
            for (int row = 0; row < chunk.Count; row++)
                _math.UpdateDuals(in values[row], context.World, chunk.Entities[row], bodies, in context, initialErrorFraction);
        }
    }
    public void AdjustBodyChange(in PhysicsStepContext context, Entity body, scoped PhysicsBodies bodies,
        in AvbdBodyEquation equation, ref Vector3 translation, ref Vector3 rotation)
    { }
}

/// <summary>Accumulates both concrete constraint types in each body equation before the common dual update.</summary>
public readonly struct AvbdConstraintGroups<TFirst, TSecond>(TFirst first, TSecond second) : IAvbdConstraints
    where TFirst : struct, IAvbdConstraints
    where TSecond : struct, IAvbdConstraints
{
    public void Bind(in PhysicsStepContext context) { first.Bind(in context); second.Bind(in context); }
    public void AddInputAccesses(World world, List<JobResourceAccess> accesses)
    { first.AddInputAccesses(world, accesses); second.AddInputAccesses(world, accesses); }
    public void AddAccesses(World world, List<JobResourceAccess> accesses)
    {
        first.AddAccesses(world, accesses);
        second.AddAccesses(world, accesses);
    }

    public void Initialize(in PhysicsStepContext context, scoped PhysicsBodies bodies, float retention)
    {
        first.Initialize(in context, bodies, retention);
        second.Initialize(in context, bodies, retention);
    }

    public void AccumulateBodyEquation(in PhysicsStepContext context, Entity body, scoped PhysicsBodies bodies, float initialErrorFraction, ref AvbdBodyEquation equation)
    {
        first.AccumulateBodyEquation(in context, body, bodies, initialErrorFraction, ref equation);
        second.AccumulateBodyEquation(in context, body, bodies, initialErrorFraction, ref equation);
    }

    public void UpdateDuals(in PhysicsStepContext context, scoped PhysicsBodies bodies, float initialErrorFraction)
    {
        first.UpdateDuals(in context, bodies, initialErrorFraction);
        second.UpdateDuals(in context, bodies, initialErrorFraction);
    }

    public void AdjustBodyChange(in PhysicsStepContext context, Entity body, scoped PhysicsBodies bodies,
        in AvbdBodyEquation equation, ref Vector3 translation, ref Vector3 rotation)
    {
        first.AdjustBodyChange(in context, body, bodies, in equation, ref translation, ref rotation);
        second.AdjustBodyChange(in context, body, bodies, in equation, ref translation, ref rotation);
    }
}

public static class AvbdConstraintGroups
{
    public static AvbdConstraintGroups<TFirst, TSecond> Combine<TFirst, TSecond>(TFirst first, TSecond second)
        where TFirst : struct, IAvbdConstraints
        where TSecond : struct, IAvbdConstraints => new(first, second);
}

internal sealed class AvbdBodyConstraints
{
    private readonly Dictionary<Entity, List<Entity>> _byBody = [];
    private readonly Stack<List<Entity>> _unused = [];

    internal void Clear()
    {
        foreach (List<Entity> entities in _byBody.Values)
        {
            entities.Clear();
            _unused.Push(entities);
        }
        _byBody.Clear();
    }

    internal void Add(Entity body, Entity constraint)
    {
        if (!_byBody.TryGetValue(body, out List<Entity>? entities))
        {
            entities = _unused.TryPop(out List<Entity>? reused) ? reused : [];
            _byBody.Add(body, entities);
        }
        if (entities.Count == 0 || entities[^1] != constraint) entities.Add(constraint);
    }

    internal ReadOnlySpan<Entity> Read(Entity body) =>
        _byBody.TryGetValue(body, out List<Entity>? entities) ? CollectionsMarshal.AsSpan(entities) : [];
}
