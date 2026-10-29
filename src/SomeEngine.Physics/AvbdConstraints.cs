using System.Runtime.InteropServices;
using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics;

/// <summary>Static numerical adaptation of a concrete entity constraint and its declared components.</summary>
public interface IAvbdConstraint<TConstraint> : IPhysicsConstraintBodies<TConstraint> where TConstraint : struct, IComponent
{
    static abstract void Initialize(in TConstraint constraint, scoped QueryRow row, scoped PhysicsBodies bodies,
        in PhysicsStepContext context, float retention);
    static abstract void AccumulateBodyEquation(in TConstraint constraint, scoped QueryRow row, Entity body,
        scoped PhysicsBodies bodies, in PhysicsStepContext context, float initialErrorFraction, ref AvbdBodyEquation equation);
    static abstract void UpdateDuals(in TConstraint constraint, scoped QueryRow row, scoped PhysicsBodies bodies,
        in PhysicsStepContext context, float initialErrorFraction);
}

public interface IAvbdConstraints : IAvbdBodyConstraints
{
    void AddAccesses(World world, List<JobResourceAccess> accesses);
    void Initialize(in PhysicsStepContext context, scoped PhysicsBodies bodies, float retention);
    void UpdateDuals(in PhysicsStepContext context, scoped PhysicsBodies bodies, float initialErrorFraction);
}

/// <summary>One concrete constraint query and reusable body-to-constraint entity indices.</summary>
public readonly struct AvbdConstraints<TConstraint, TMath> : IAvbdConstraints
    where TConstraint : struct, IComponent
    where TMath : struct, IAvbdConstraint<TConstraint>
{
    private readonly QueryHandle _constraints;
    private readonly AvbdBodyConstraints _byBody;

    public AvbdConstraints(QueryHandle constraints)
    {
        _constraints = constraints;
        _byBody = new();
    }

    public void AddAccesses(World world, List<JobResourceAccess> accesses)
    {
        PhysicsQueryAccess.AddAccesses(world, _constraints, accesses);
        accesses.Add(JobResourceAccess.Exclusive(WorldJobs.GetScheduler(world).GetResourceToken(_byBody)));
    }

    public void Initialize(in PhysicsStepContext context, scoped PhysicsBodies bodies, float retention)
    {
        _byBody.Clear();
        var execution = new InitializeConstraints(bodies, context, retention, _byBody);
        context.World.ExecuteQuery(_constraints, ref execution);
    }

    public void AccumulateBodyEquation(in PhysicsStepContext context, Entity body, scoped PhysicsBodies bodies, float initialErrorFraction, ref AvbdBodyEquation equation)
    {
        var execution = new AddBodyEquations(body, bodies, context, initialErrorFraction, _byBody.Read(body), ref equation);
        context.World.ExecuteQuery(_constraints, ref execution);
    }

    public void UpdateDuals(in PhysicsStepContext context, scoped PhysicsBodies bodies, float initialErrorFraction)
    {
        var execution = new UpdateConstraintDuals(bodies, context, initialErrorFraction);
        context.World.ExecuteQuery(_constraints, ref execution);
    }

    public void AdjustBodyChange(in PhysicsStepContext context, Entity body, scoped PhysicsBodies bodies,
        in AvbdBodyEquation equation, ref Vector3 translation, ref Vector3 rotation) { }

    private readonly ref struct InitializeConstraints(PhysicsBodies bodies, PhysicsStepContext context,
        float retention, AvbdBodyConstraints byBody) : IQueryExecution
    {
        private readonly PhysicsBodies _bodies = bodies;
        private readonly PhysicsStepContext _context = context;

        public void Execute(scoped QueryCursor query)
            => AvbdConstraintRows<TConstraint, TMath>.Initialize(new(query), _bodies, in _context, retention, byBody);
    }

    private ref struct AddBodyEquations : IQueryExecution
    {
        private readonly Entity _body;
        private readonly PhysicsBodies _bodies;
        private readonly PhysicsStepContext _context;
        private readonly float _initialErrorFraction;
        private readonly ReadOnlySpan<Entity> _constraints;
        private ref AvbdBodyEquation _equation;

        internal AddBodyEquations(Entity body, PhysicsBodies bodies, PhysicsStepContext context, float initialErrorFraction,
            ReadOnlySpan<Entity> constraints, ref AvbdBodyEquation equation)
        {
            _body = body;
            _bodies = bodies;
            _context = context;
            _initialErrorFraction = initialErrorFraction;
            _constraints = constraints;
            _equation = ref equation;
        }

        public void Execute(scoped QueryCursor query)
            => AvbdConstraintRows<TConstraint, TMath>.Accumulate(new(query), _body, _bodies, in _context,
                _initialErrorFraction, _constraints, ref _equation);
    }

    private readonly ref struct UpdateConstraintDuals(PhysicsBodies bodies, PhysicsStepContext context,
        float initialErrorFraction) : IQueryExecution
    {
        private readonly PhysicsBodies _bodies = bodies;
        private readonly PhysicsStepContext _context = context;

        public void Execute(scoped QueryCursor query)
            => AvbdConstraintRows<TConstraint, TMath>.UpdateDuals(new(query), _bodies, in _context, initialErrorFraction);
    }
}

/// <summary>Accumulates both concrete constraint types in each body equation before the common dual update.</summary>
public readonly struct AvbdConstraintGroups<TFirst, TSecond>(TFirst first, TSecond second) : IAvbdConstraints
    where TFirst : struct, IAvbdConstraints
    where TSecond : struct, IAvbdConstraints
{
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
