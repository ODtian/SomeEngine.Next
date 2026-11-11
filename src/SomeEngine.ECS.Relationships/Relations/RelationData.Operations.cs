using SomeEngine.ECS.Entities;

namespace SomeEngine.ECS.Relations;

internal sealed partial class RelationData<T>
{
    private void ConfirmApplied()
    {
        if (!_hasDeferredChanges) _version = _world.Version;
    }

    internal void Initialize()
    {
        var entities = new List<Entity>();
        using (var scope = _main.Open())
            foreach (var rows in scope) entities.AddRange(rows.Entities);
        foreach (Entity entity in entities) InitializeEdge(entity);
        _version = _world.Version;
    }

    internal void ValidateCreate(in RelationEndpoints<T> value)
    {
        _world.RequireStructure();
        EnsureCapacity();
        ValidateEndpoints(value);
        ValidateClaims(Entity.Null, value);
        ReserveApplication(default, value);
    }

    internal void InitializeEdge(Entity edge)
    {
        PrepareApplied(edge);
        ReadMain(edge, out var value);
        ValidateEndpoints(value);
        ValidateClaims(edge, value);
        UpdateFuture(edge, default, value, false);
        Claim(edge, value);
        Apply(edge, value);
        ConfirmApplied();
    }

    internal void Retarget(Entity edge, in RelationEndpoints<T> after, bool deferred)
    {
        _world.RequireStructure();
        RequireActive(edge);
        EnsureCapacity();
        if (!ReadMain(edge, out var before)) throw new ArgumentException("The entity requires this relation's endpoints.");
        ValidateEndpoints(after);
        ValidateClaims(edge, after);
        if (!deferred) ReserveApplication(Applied(edge), after);
        UpdateFuture(edge, before, after, deferred);
        ReleaseClaims(edge, before);
        ref EntityRecord record = ref _world.Resolve(edge);
        _world.SetValueKnown(edge, ref record, after);
        Claim(edge, after);
        if (deferred)
        {
            _hasDeferredChanges = true;
            AppliedEdge applied = Applied(edge);
            _sourceDifferences.Set(edge, after.Source == applied.Source ? Entity.Null : after.Source);
            _targetDifferences.Set(edge, after.Target == applied.Target ? Entity.Null : after.Target);
        }
        else
        {
            Apply(edge, after, false);
            ConfirmApplied();
        }
    }

    internal ReadOnlySpan<RelationEntry> ReadEntries(Entity endpoint, RelationAdjacencyRole role)
        => Active(endpoint) ? ReadHead(endpoint, role).Entries : [];

    internal Entity FindPair(Entity source, Entity target)
    {
        if (!Active(source) || !Active(target)) return Entity.Null;
        if (_uniquePair)
        {
            Entity edge = _pairs.GetValueOrDefault(EndpointPair.From(source, target, _directed));
            return Active(edge) ? edge : Entity.Null;
        }
        var role = _directed ? RelationAdjacencyRole.Outgoing : RelationAdjacencyRole.Incident;
        foreach (RelationEntry entry in ReadHead(source, role).Entries)
            if (entry.Other == target) return entry.Edge;
        return Entity.Null;
    }

    internal void DestroyEdge(Entity edge)
    {
        RequireActive(edge);
        if (!ReadMain(edge, out _)) throw new ArgumentException("The entity requires this relation's endpoints.");
        _world.Destroy(edge);
    }
}
