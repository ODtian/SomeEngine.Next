using System.Runtime.InteropServices;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics;

/// <summary>Removes selected constraints whose native endpoint links ended with a body.</summary>
public readonly struct MaintainConstraintBodies : IPhysicsStage, IDisposable
{
    private readonly ConstraintBodyChanges _changes;
    public MaintainConstraintBodies(World world, Query constraints) => _changes = new(world, constraints);
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => _changes.Schedule(dependency);
    public void Dispose() => _changes.Clear();
}
internal sealed class ConstraintBodyChanges
{
    private readonly World _world;
    private readonly Query _constraints, _removed;
    private readonly HashSet<Entity> _members = [], _destroyed = [];
    private readonly List<Entity> _destroy = [], _cleanup = [];
    internal ConstraintBodyChanges(World world, Query constraints)
    {
        _world = world; _constraints = constraints;
        _removed = world.Query(new QueryDescription().WithStored<Removed<RelationEndpoints<ConstraintBodyLink>>>());
    }
    internal JobHandle Schedule(JobHandle dependency) => (_world.Execution!.Scheduler!).Schedule(new Maintain(this),
        ((WorldJobs)_world.Execution!).StructureWrite, dependency: dependency);
    private readonly struct Maintain(ConstraintBodyChanges changes) : IJob { public void Execute() => changes.Apply(); }
    private bool IsActive(Entity entity) => _world.IsAlive(entity) && !_world.Has<Destroyed>(entity);
    private void Apply()
    {
        Clear();
        using (QueryScope scope = _constraints.OpenRead())
            foreach (QueryChunk chunk in scope)
                foreach (Entity entity in chunk.Entities) _members.Add(entity);
        using (QueryScope scope = _removed.OpenRead())
            foreach (QueryChunk chunk in scope)
            {
                ReadOnlySpan<Removed<RelationEndpoints<ConstraintBodyLink>>> values = chunk.Read<Removed<RelationEndpoints<ConstraintBodyLink>>>();
                foreach (var removed in values)
                {
                    RelationEndpoints<ConstraintBodyLink> endpoints = removed.Value;
                    if (_members.Contains(endpoints.Source) && !IsActive(endpoints.Target) && _destroyed.Add(endpoints.Source))
                        _destroy.Add(endpoints.Source);
                }
            }
        _world.Destroy(CollectionsMarshal.AsSpan(_destroy));
        using (QueryScope scope = _removed.OpenRead())
            foreach (QueryChunk chunk in scope)
            {
                ReadOnlySpan<Removed<RelationEndpoints<ConstraintBodyLink>>> values = chunk.Read<Removed<RelationEndpoints<ConstraintBodyLink>>>();
                for (int row = 0; row < chunk.Count; row++)
                {
                    RelationEndpoints<ConstraintBodyLink> endpoints = values[row].Value;
                    if (_members.Contains(endpoints.Source) || !IsActive(endpoints.Source) || IsActive(endpoints.Target))
                        _cleanup.Add(chunk.Entities[row]);
                }
            }
        foreach (Entity edge in _cleanup) _world.Remove<Removed<RelationEndpoints<ConstraintBodyLink>>>(edge);
    }
    internal void Clear() { _members.Clear(); _destroyed.Clear(); _destroy.Clear(); _cleanup.Clear(); }
}
