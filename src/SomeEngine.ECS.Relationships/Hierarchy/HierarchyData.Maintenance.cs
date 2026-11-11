using SomeEngine.ECS.Entities;
using System.Runtime.InteropServices;
using SomeEngine.ECS.Storage;

namespace SomeEngine.ECS.Hierarchy;

internal sealed partial class HierarchyData<TDomain>
{
    private readonly List<Entity> _incoming = new(), _subtree = new();
    private readonly HashSet<Entity> _maintenanceChanges = new();
    private readonly Dictionary<Entity, Entity> _lastOrderedRemovals = new();

    private List<Entity>? Incoming(Entity parent)
    {
        Children<TDomain> children = _retiredChildren.TryGetValue(parent, out var retired) ? retired : ReadChildren(parent);
        ReadOnlySpan<Entity> differences = _differences.Members(parent);
        if (children.Count == 0 && differences.IsEmpty) return null;
        var entities = _incoming;
        entities.Clear(); entities.EnsureCapacity(children.Count + differences.Length);
        entities.AddRange(children.Entities);
        entities.AddRange(differences);
        return entities;
    }

    private void RemoveIncoming(Entity parent)
    {
        if (Incoming(parent) is not { } incoming) return;
        Entity last = Entity.Null;
        foreach (Entity child in incoming)
        {
            if (Applied(child).Parent == parent) last = child;
            if (!Active(child)) { Apply(child, Entity.Null, -1, emptyingParent: parent); continue; }
            Entity current = ReadParent(child);
            if (current == parent || current != Entity.Null && !Active(current))
                SetParent(child, Entity.Null, false, -1, false, parent);
            else Apply(child, current, -1, emptyingParent: parent);
        }
        RemoveChildrenHead(parent, last);
    }

    private void RemoveChildrenHead(Entity parent, Entity last)
    {
        if (_retiredChildren.Remove(parent, out var retired)) { Children<TDomain>.Release(ref retired); return; }
        Children<TDomain> current = ReadChildren(parent);
        if (current.Count == 0) return;
        current.Items![0] = last;
        ChildrenRow(parent, out _, out _) = new(current.Items, 1, current.IsOrdered);
        if (current.IsOrdered) _emptyOrdered.Add(parent);
        _world.RemoveValue<Children<TDomain>>(parent);
    }

    public bool RequiresIndividualDestroy(Archetype archetype)
        => _hasDeferredChanges || _version != _world.Version || _usesChildOrder
            || archetype.Types.Bits.Has(ComponentType<Parent<TDomain>>.TypeId)
            || archetype.Types.Bits.Has(ComponentType<Children<TDomain>>.TypeId);

    public void BeforeDestroy(Entity entity, bool deferred)
    {
        if (!_hasDeferredChanges && _version == _world.Version)
        {
            ref EntityRecord record = ref _world.Records[entity.Id];
            var columns = record.Chunk!.Columns;
            int parentType = ComponentType<Parent<TDomain>>.TypeId;
            int childrenType = ComponentType<Children<TDomain>>.TypeId;
            if (((uint)parentType >= (uint)columns.Length || !columns[parentType].Has(record.Row))
                && ((uint)childrenType >= (uint)columns.Length || !columns[childrenType].Has(record.Row)))
            {
                if (_usesChildOrder) _emptyOrdered.Remove(entity);
                return;
            }
        }
        EnsureCapacity();
        Entity parent = ReadParent(entity);
        UpdateProjected(entity, parent, Entity.Null, false, -1);
        ReleaseProjected(entity);
        if (deferred)
        {
            _hasDeferredChanges = true;
            Children<TDomain> children = ReadChildren(entity);
            if (children.Count != 0) _retiredChildren.Add(entity, Children<TDomain>.Copy(children));
            _differences.Remove(entity);
        }
        else
        {
            RemoveIncoming(entity);
            if (parent != Entity.Null) _world.RemoveValue<Parent<TDomain>>(entity);
            Apply(entity, Entity.Null, -1);
        }
        _emptyOrdered.Remove(entity);
        if (!deferred) ConfirmAppliedParents();
    }

    private void ConsumeDestroyed(Entity entity)
    {
        if (_retiredChildren.Count == 0 && _retiredApplied.Count == 0 && Applied(entity).Parent == Entity.Null
            && _differences.Members(entity).IsEmpty) return;
        RemoveIncoming(entity);
        Apply(entity, Entity.Null, -1);
        _retiredApplied.Remove(entity);
        if (_retiredChildren.Remove(entity, out var children)) Children<TDomain>.Release(ref children);
        ReleaseProjected(entity);
    }

    internal void Maintain()
    {
        _world.RequireStructure();
        using var version = _world.BeginBatchVersion(0);
        EnsureCapacity();
        using DestroyedRead destroyed = _reader.Open();
        ReadOnlySpan<DestroyedEntity> first = destroyed.First, second = destroyed.Second;
        ulong through = destroyed.Through;
        foreach (DestroyedEntity fact in first) ConsumeDestroyed(fact.Entity);
        foreach (DestroyedEntity fact in second) ConsumeDestroyed(fact.Entity);
        var entities = _maintenanceChanges;
        entities.Clear();
        using (var scope = _changes.Since(_version).Open())
            foreach (var rows in scope) foreach (Entity entity in rows.Entities) entities.Add(entity);
        using (var scope = _removed.Open())
            foreach (var rows in scope)
            {
                var values = rows.Read<Removed<Parent<TDomain>>>();
                for (int i = 0; i < rows.Count; i++) if (values[i].Version > _version) entities.Add(rows.Entities[i]);
            }
        var lastOrdered = _lastOrderedRemovals; lastOrdered.Clear();
        foreach (Entity entity in entities)
        {
            Entity before = Applied(entity).Parent;
            bool active = Active(entity);
            Entity parent = active ? ReadParentKnown(entity) : Entity.Null;
            if (before != parent && _projected.ContainsKey(before)) lastOrdered[before] = entity;
            if (!active) { Apply(entity, Entity.Null, -1, true); continue; }
            PrepareApplied(entity);
            if (parent != Entity.Null && !Active(parent))
                SetParent(entity, Entity.Null, false, -1, false);
            else Apply(entity, parent, -1, true);
        }
        foreach (var pair in _projected)
        {
            if (!Active(pair.Key)) continue;
            List<Entity> order = pair.Value;
            int kept = 0;
            for (int i = 0; i < order.Count; i++)
                if (Applied(order[i]).Parent == pair.Key) order[kept++] = order[i];
            order.RemoveRange(kept, order.Count - kept);
            if (kept == 0) { RemoveChildrenHead(pair.Key, lastOrdered.GetValueOrDefault(pair.Key)); continue; }
            EnsureChildrenCapacity(pair.Key, kept);
            WriteOrder(pair.Key, order, true);
        }
        ClearProjected();
        _hasDeferredChanges = false;
        _version = _world.Version;
        _reader.Advance(through);
    }

    internal void DestroySubtree(Entity root)
    {
        _world.RequireStructure();
        RequireActive(_world, root);
        var entities = _subtree;
        entities.Clear(); entities.Add(root);
        for (int i = 0; i < entities.Count; i++)
        {
            Entity parent = entities[i];
            foreach (Entity child in ReadChildren(parent).Entities)
                if (Active(child) && ReadParent(child) == parent) entities.Add(child);
            foreach (Entity child in _differences.Members(parent))
                if (Active(child)) entities.Add(child);
        }
        entities.Reverse();
        _world.Destroy(CollectionsMarshal.AsSpan(entities));
    }

    public void Clear() => ClearData(true);

    private void ClearData(bool clearApplied)
    {
        foreach (var pair in _retiredChildren)
        {
            Children<TDomain> value = pair.Value;
            Children<TDomain>.Release(ref value);
        }
        _retiredChildren.Clear(); _retiredApplied.Clear(); ClearProjected(); _emptyOrdered.Clear();
        _incoming.Clear(); _subtree.Clear(); _maintenanceChanges.Clear();
        _lastOrderedRemovals.Clear();
        _batchEdits.Clear(); _batchSeen.Clear(); _batchColumns.Clear(); _batchHeads.Clear(); _batchTargets.Clear();
        _batchAppliedOrders.Clear(); _batchFutureOrders.Clear(); _batchMainParents.Clear(); _batchAppliedParents.Clear();
        _batchMainArrivals.Clear(); _batchAppliedArrivals.Clear();
        _batchSources.Clear();
        foreach (List<Entity> children in _batchChildren) children.Clear();
        _batchChildrenCount = 0;
        _usesChildOrder = false;
        _hasDeferredChanges = false;
        _differences.Clear();
        if (clearApplied) Array.Clear(_applied, 0, Math.Min(_applied.Length, _world.Used + 1));
        _version = _world.Version;
    }

    public void Dispose() { ClearData(false); _reader.Dispose(); _applied = []; _spareChildOrders.Clear(); _batchChildren.Clear(); }
}
