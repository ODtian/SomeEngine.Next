using System.Buffers;
using SomeEngine.ECS.Entities;

namespace SomeEngine.ECS.Relations;

internal sealed partial class RelationData<T>
{
    private void RemoveIncidents(Entity endpoint)
    {
        var first = ReadHead(endpoint, _directed ? RelationAdjacencyRole.Outgoing : RelationAdjacencyRole.Incident);
        var second = _directed ? ReadHead(endpoint, RelationAdjacencyRole.Incoming) : default;
        var sources = _sourceDifferences.Members(endpoint);
        var targets = _targetDifferences.Members(endpoint);
        int count = first.Count + second.Count + sources.Length + targets.Length;
        if (count == 0) return;
        Entity[] entities = ArrayPool<Entity>.Shared.Rent(count);
        int used = 0;
        foreach (RelationEntry entry in first.Entries) entities[used++] = entry.Edge;
        foreach (RelationEntry entry in second.Entries) entities[used++] = entry.Edge;
        sources.CopyTo(entities.AsSpan(used)); used += sources.Length;
        targets.CopyTo(entities.AsSpan(used));
        try
        {
            foreach (Entity edge in entities.AsSpan(0, count))
            {
                if (!Active(edge) || !ReadMain(edge, out var current)) continue;
                if (current.Source == endpoint || current.Target == endpoint || !Active(current.Source) || !Active(current.Target))
                    _world.Destroy(edge);
                else Apply(edge, current);
            }
        }
        finally { ArrayPool<Entity>.Shared.Return(entities); }
    }

    public void BeforeDestroy(Entity entity, bool deferred)
    {
        EnsureCapacity();
        if (ReadMain(entity, out var main))
        {
            UpdateFuture(entity, main, default, deferred);
            ReleaseClaims(entity, main);
            _sourceDifferences.Remove(entity);
            _targetDifferences.Remove(entity);
            if (!deferred)
            {
                _world.RemoveValue<RelationEndpoints<T>>(entity);
                Apply(entity, default);
            }
        }
        if (deferred) _hasDeferredChanges = true;
        else
        {
            RemoveIncidents(entity);
            if (_usesOrder)
            {
                _futureOrders.Remove(new(entity, _directed ? RelationAdjacencyRole.Outgoing : RelationAdjacencyRole.Incident));
                if (_directed) _futureOrders.Remove(new(entity, RelationAdjacencyRole.Incoming));
            }
            ConfirmApplied();
        }
    }

    private void ConsumeDestroyed(Entity entity)
    {
        Apply(entity, default);
        RemoveIncidents(entity);
        _retiredApplied.Remove(entity);
    }

    internal void Maintain()
    {
        _world.RequireStructure();
        EnsureCapacity();
        using DestroyedRead destroyed = _reader.Open();
        ReadOnlySpan<DestroyedEntity> first = destroyed.First, second = destroyed.Second;
        ulong through = destroyed.Through;
        if (!_hasDeferredChanges)
        {
            _version = _world.Version;
            _reader.Advance(through);
            return;
        }
        foreach (DestroyedEntity fact in first) ConsumeDestroyed(fact.Entity);
        foreach (DestroyedEntity fact in second) ConsumeDestroyed(fact.Entity);
        var entities = _maintenanceChanges;
        entities.Clear();
        using (var scope = _changes.Since(_version).Open())
            foreach (var rows in scope) foreach (Entity entity in rows.Entities) entities.Add(entity);
        using (var scope = _removed.Open())
            foreach (var rows in scope)
            {
                var values = rows.Read<Removed<RelationEndpoints<T>>>();
                for (int i = 0; i < rows.Count; i++) if (values[i].Version > _version) entities.Add(rows.Entities[i]);
            }
        bool groupedOrder = _futureOrders.Count != 0;
        if (groupedOrder)
        {
            foreach (Entity edge in entities)
            {
                if (Active(edge) && ReadMain(edge, out var current) && (!Active(current.Source) || !Active(current.Target)))
                    _world.Destroy(edge);
            }
            foreach (var pair in _futureOrders)
                if (Active(pair.Key.Entity)) ReserveHead(pair.Key.Entity, pair.Key.Role, pair.Value.Count);
        }
        foreach (Entity edge in entities)
        {
            if (!Active(edge) || !ReadMain(edge, out var current)) { Apply(edge, default, groupedOrder: groupedOrder); continue; }
            if (!groupedOrder && (!Active(current.Source) || !Active(current.Target))) _world.Destroy(edge);
            else Apply(edge, current, groupedOrder: groupedOrder);
        }
        PublishFutureOrders();
        _hasDeferredChanges = false;
        _version = _world.Version;
        _reader.Advance(through);
    }

    public void Clear() => ClearData(true);

    private void ClearData(bool clearArrays)
    {
        int entities = _world.Used + 1;
        int outgoing = Math.Min(_outgoing.Length, entities), incoming = Math.Min(_incoming.Length, entities);
        foreach (AdjacencyHead head in _outgoing.AsSpan(0, outgoing)) if (head.Items is { } items) AdjacencyHead.Return(items);
        foreach (AdjacencyHead head in _incoming.AsSpan(0, incoming)) if (head.Items is { } items) AdjacencyHead.Return(items);
        foreach (AdjacencyHead head in _retiredHeads.Values) if (head.Items is { } items) AdjacencyHead.Return(items);
        if (clearArrays)
        {
            Array.Clear(_outgoing, 0, outgoing); Array.Clear(_incoming, 0, incoming);
            Array.Clear(_applied, 0, Math.Min(_applied.Length, entities));
            Array.Clear(_sourceClaims, 0, Math.Min(_sourceClaims.Length, entities));
            Array.Clear(_targetClaims, 0, Math.Min(_targetClaims.Length, entities));
        }
        _pairs.Clear(); _retiredHeads.Clear(); _retiredApplied.Clear(); _maintenanceChanges.Clear();
        _sourceDifferences.Clear(); _targetDifferences.Clear();
        ClearBatch();
        ClearOrder();
        _hasDeferredChanges = false;
        _version = _world.Version;
    }

    public void Dispose()
    {
        ClearData(false);
        _reader.Dispose();
        _outgoing = _incoming = [];
        _applied = [];
        _sourceClaims = _targetClaims = [];
    }
}
