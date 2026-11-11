using System.Buffers;

namespace SomeEngine.ECS.Comparison;

internal sealed class ReferenceVersionClock { internal ulong Value; }

[Friflo.Engine.ECS.GenericInstanceType("ReferenceParent", typeof(Friflo.Engine.ECS.Entity))]
[Friflo.Engine.ECS.GenericInstanceType("ArchiveParent", typeof(SomeEngine.ECS.Entities.Entity))]
public struct ReferenceParent<TEntity> : EasyEcs.IComponent, Friflo.Engine.ECS.IComponent where TEntity : struct
{
    public TEntity Value;
    public ulong Added, Written;
}

[Friflo.Engine.ECS.GenericInstanceType("ReferenceChildren", typeof(Friflo.Engine.ECS.Entity))]
[Friflo.Engine.ECS.GenericInstanceType("ArchiveChildren", typeof(SomeEngine.ECS.Entities.Entity))]
public struct ReferenceChildren<TEntity> : EasyEcs.IComponent, Friflo.Engine.ECS.IComponent where TEntity : struct
{
    public TEntity[]? Items;
    public int Count;
    public ulong Added, Written;
}

[Friflo.Engine.ECS.GenericInstanceType("ReferenceOldParent", typeof(Friflo.Engine.ECS.Entity))]
[Friflo.Engine.ECS.GenericInstanceType("ArchiveOldParent", typeof(SomeEngine.ECS.Entities.Entity))]
public struct ReferenceOldParent<TEntity> : EasyEcs.IComponent, Friflo.Engine.ECS.IComponent where TEntity : struct
{
    public TEntity Value;
    public ulong Version;
}

[Friflo.Engine.ECS.GenericInstanceType("ReferenceOldChildren", typeof(Friflo.Engine.ECS.Entity))]
[Friflo.Engine.ECS.GenericInstanceType("ArchiveOldChildren", typeof(SomeEngine.ECS.Entities.Entity))]
public struct ReferenceOldChildren<TEntity> : EasyEcs.IComponent, Friflo.Engine.ECS.IComponent where TEntity : struct
{
    public TEntity[]? Items;
    public int Count;
    public ulong Version;
}

internal interface IHierarchyValues<TEntity> : IDisposable where TEntity : struct
{
    int Index(TEntity entity);
    bool Alive(TEntity entity);
    bool Has<T>(TEntity entity) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent;
    T Read<T>(TEntity entity) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent;
    void Set<T>(TEntity entity, in T value) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent;
    void Remove<T>(TEntity entity) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent;
    TEntity Create(int ordinal);
    void Destroy(TEntity entity);
    void ReadTopology(HierarchyObservation<TEntity> result);
}

internal sealed partial class ReferenceHierarchy<TEntity, TValues> : IHierarchyComparison<TEntity>
    where TEntity : struct where TValues : struct, IHierarchyValues<TEntity>
{
    private struct Applied
    {
        internal TEntity Entity, Parent;
        internal int Place;
    }

    private readonly TValues _values;
    private readonly TEntity[] _entities;
    private Applied[] _applied;
    private TEntity[] _targets;
    private readonly Dictionary<TEntity, HashSet<TEntity>> _differences = new();
    private readonly Stack<HashSet<TEntity>> _differenceSets = new();
    private readonly HashSet<TEntity> _pending = new(), _destroyed = new();
    private readonly HashSet<TEntity> _ordered = new();
    private readonly List<TEntity> _oldParents = new(), _oldChildren = new();
    private readonly Dictionary<TEntity, TEntity> _edits = new();
    private readonly Dictionary<TEntity, int> _forestSeen = new(), _headCounts = new();
    private readonly Dictionary<TEntity, ReferenceChildren<TEntity>> _orderedHeads = new();
    private readonly Dictionary<TEntity, TEntity> _lastRemovals = new();
    private readonly List<TEntity> _headKeys = new(), _incoming = new(), _subtree = new();
    private readonly ReferenceVersionClock? _clock;
    private ulong _version;
    public ulong Version
    {
        get => _clock?.Value ?? _version;
        private set { if (_clock is { } clock) clock.Value = value; else _version = value; }
    }
    private static bool Equal(TEntity a, TEntity b) => EqualityComparer<TEntity>.Default.Equals(a, b);
    private static bool Empty(TEntity entity) => Equal(entity, default);

    internal ReferenceHierarchy(TValues values, TEntity[] entities, ReferenceVersionClock? clock = null)
    {
        _values = values; _entities = entities; _clock = clock;
        int capacity = entities.Length + ComparisonWorld.Changes + 1;
        _applied = new Applied[capacity]; _targets = new TEntity[capacity];
    }

    public bool Active(TEntity entity) => _values.Alive(entity) && !_destroyed.Contains(entity);
    public TEntity Parent(TEntity entity) => _values.Has<ReferenceParent<TEntity>>(entity)
        ? _values.Read<ReferenceParent<TEntity>>(entity).Value : default;
    private ReferenceChildren<TEntity> Children(TEntity entity) => _values.Has<ReferenceChildren<TEntity>>(entity)
        ? _values.Read<ReferenceChildren<TEntity>>(entity) : default;

    private int Prepare(TEntity entity)
    {
        int index = _values.Index(entity);
        if (index >= _applied.Length)
        {
            int capacity = Math.Max(index + 1, _applied.Length * 2);
            Array.Resize(ref _applied, capacity); Array.Resize(ref _targets, capacity);
        }
        if (!Equal(_applied[index].Entity, entity)) _applied[index] = new() { Entity = entity };
        return index;
    }
    private void RequireActive(TEntity entity)
    {
        if (!Active(entity)) throw new ArgumentException("Hierarchy entity has completed its active lifetime.");
    }
    public void ReadTopology(HierarchyObservation<TEntity> result) => _values.ReadTopology(result);
    public int ChildCount(TEntity entity) => Children(entity).Count;
    public void CopyChildren(TEntity entity, Span<TEntity> children)
    { var values = Children(entity); values.Items.AsSpan(0, values.Count).CopyTo(children); }

    private void Main(TEntity entity, TEntity parent, ulong version)
    {
        bool present = _values.Has<ReferenceParent<TEntity>>(entity);
        if (Empty(parent))
        {
            if (!present) return;
            if (!_values.Has<ReferenceOldParent<TEntity>>(entity)) _oldParents.Add(entity);
            _values.Set(entity, new ReferenceOldParent<TEntity> { Value = Parent(entity), Version = version });
            _values.Remove<ReferenceParent<TEntity>>(entity);
            return;
        }
        ulong added = present ? _values.Read<ReferenceParent<TEntity>>(entity).Added : version;
        _values.Set(entity, new ReferenceParent<TEntity> { Value = parent, Added = added, Written = version });
    }

    private void Capacity(TEntity parent, int count, ulong version)
    {
        var children = Children(parent);
        if (children.Items is null)
        {
            children = new() { Items = ArrayPool<TEntity>.Shared.Rent(Math.Max(4, count)), Added = version, Written = version };
        }
        else if (children.Items.Length < count)
        {
            TEntity[] items = ArrayPool<TEntity>.Shared.Rent(Math.Max(count, children.Items.Length * 2));
            children.Items.AsSpan(0, children.Count).CopyTo(items);
            ArrayPool<TEntity>.Shared.Return(children.Items);
            children.Items = items;
        }
        else return;
        _values.Set(parent, children);
    }

    private void RemoveChildren(TEntity parent, ulong version)
    {
        var children = Children(parent);
        if (_values.Has<ReferenceOldChildren<TEntity>>(parent))
        {
            var previous = _values.Read<ReferenceOldChildren<TEntity>>(parent);
            ArrayPool<TEntity>.Shared.Return(previous.Items!);
        }
        else _oldChildren.Add(parent);
        _values.Set(parent, new ReferenceOldChildren<TEntity> { Items = children.Items, Count = children.Count, Version = version });
        _values.Remove<ReferenceChildren<TEntity>>(parent);
    }

    private void Drop(TEntity parent, int place, bool keepEmpty, ulong version)
    {
        var children = Children(parent);
        if (children.Count == 1 && !keepEmpty) { RemoveChildren(parent, version); return; }
        int last = --children.Count;
        if (_ordered.Contains(parent))
        {
            children.Items!.AsSpan(place + 1, last - place).CopyTo(children.Items.AsSpan(place));
            for (int i = place; i < last; i++) _applied[_values.Index(children.Items![i])].Place = i;
        }
        else if (place != last)
        {
            TEntity moved = children.Items![last];
            children.Items[place] = moved;
            _applied[_values.Index(moved)].Place = place;
        }
        children.Written = version;
        _values.Set(parent, children);
    }

    private int Append(TEntity parent, TEntity child, ulong version)
    {
        var children = Children(parent);
        int place = children.Count++;
        children.Items![place] = child;
        children.Written = version;
        _values.Set(parent, children);
        return place;
    }

    private void Difference(TEntity child, TEntity parent)
    {
        int index = Prepare(child);
        TEntity old = _targets[index];
        if (Equal(old, parent)) return;
        if (!Empty(old))
        {
            var members = _differences[old];
            members.Remove(child);
            if (members.Count == 0) { _differences.Remove(old); _differenceSets.Push(members); }
        }
        _targets[index] = parent;
        if (Empty(parent)) return;
        if (!_differences.TryGetValue(parent, out var target))
        {
            target = _differenceSets.TryPop(out var reusable) ? reusable : new();
            _differences.Add(parent, target);
        }
        target.Add(child);
    }

    private void Apply(TEntity child, TEntity parent, ulong version, TEntity emptyingParent = default)
    {
        int index = Prepare(child);
        Applied before = _applied[index];
        if (!Equal(before.Parent, parent))
        {
            if (!Empty(parent)) Capacity(parent, Children(parent).Count + 1, version);
            if (!Empty(before.Parent) && !Equal(before.Parent, emptyingParent)) Drop(before.Parent, before.Place, false, version);
            int place = Empty(parent) ? 0 : Append(parent, child, version);
            _applied[index] = new() { Entity = child, Parent = parent, Place = place };
        }
        Difference(child, default);
    }

    public void SetParents(ReadOnlySpan<HierarchyEdit<TEntity>> edits, bool deferred)
    {
        var after = _edits;
        after.Clear(); after.EnsureCapacity(edits.Length);
        foreach (var edit in edits)
        {
            RequireActive(edit.Child);
            if (!Empty(edit.Parent)) RequireActive(edit.Parent);
            after[edit.Child] = edit.Parent;
        }
        var seen = _forestSeen;
        seen.Clear();
        int start = 0;
        foreach (TEntity child in after.Keys)
        {
            start++;
            for (TEntity next = child; !Empty(next); next = after.TryGetValue(next, out var target) ? target : Parent(next))
            {
                if (seen.TryGetValue(next, out int previous))
                {
                    if (previous == start) throw new ArgumentException("Parent links form a cycle.");
                    break;
                }
                seen.Add(next, start);
            }
        }
        ulong version = ++Version;
        if (deferred)
        {
            foreach (var edit in after)
            {
                int index = Prepare(edit.Key);
                Main(edit.Key, edit.Value, version);
                Difference(edit.Key, Equal(_applied[index].Parent, edit.Value) ? default : edit.Value);
                _pending.Add(edit.Key);
            }
            return;
        }
        ApplyBatch(after, version, true);
    }

    private void ApplyBatch(Dictionary<TEntity, TEntity> after, ulong version, bool writeParents)
    {
        var counts = _headCounts;
        counts.Clear(); _orderedHeads.Clear(); _lastRemovals.Clear();
        foreach (var edit in after)
        {
            int index = Prepare(edit.Key);
            TEntity old = _applied[index].Parent;
            if (Equal(old, edit.Value)) continue;
            if (!Empty(old))
            {
                counts[old] = counts.GetValueOrDefault(old) - 1;
                _lastRemovals[old] = edit.Key;
            }
            if (!Empty(edit.Value)) counts[edit.Value] = counts.GetValueOrDefault(edit.Value) + 1;
        }
        _headKeys.Clear(); _headKeys.AddRange(counts.Keys);
        foreach (TEntity parent in _headKeys) counts[parent] += Children(parent).Count;
        foreach (var head in counts) if (head.Value > 0) Capacity(head.Key, head.Value, version);
        foreach (var head in counts)
        {
            if (head.Value == 0 || !_ordered.Contains(head.Key)) continue;
            var children = Children(head.Key);
            int kept = 0;
            if (_lastRemovals.ContainsKey(head.Key))
            {
                for (int i = 0; i < children.Count; i++)
                {
                    TEntity child = children.Items![i];
                    if (after.TryGetValue(child, out var target) && !Equal(target, head.Key)) continue;
                    children.Items[kept] = child;
                    _applied[_values.Index(child)].Place = kept++;
                }
                children.Count = kept;
            }
            _orderedHeads.Add(head.Key, children);
        }
        foreach (var edit in after)
        {
            int index = Prepare(edit.Key);
            Applied before = _applied[index];
            if (!Equal(before.Parent, edit.Value) && !Empty(before.Parent) && counts[before.Parent] > 0
                && !_orderedHeads.ContainsKey(before.Parent))
                Drop(before.Parent, before.Place, true, version);
        }
        foreach (var head in counts)
        {
            if (head.Value != 0) continue;
            if (!writeParents)
            {
                var children = Children(head.Key);
                children.Items![0] = _lastRemovals[head.Key]; children.Count = 1;
                _values.Set(head.Key, children);
            }
            RemoveChildren(head.Key, version);
        }
        if (writeParents) foreach (var edit in after) Main(edit.Key, edit.Value, version);
        foreach (var edit in after)
        {
            int index = Prepare(edit.Key);
            if (!Equal(_applied[index].Parent, edit.Value))
            {
                int place = 0;
                if (!Empty(edit.Value))
                {
                    if (_orderedHeads.TryGetValue(edit.Value, out var children))
                    {
                        place = children.Count++;
                        children.Items![place] = edit.Key;
                        _orderedHeads[edit.Value] = children;
                    }
                    else place = Append(edit.Value, edit.Key, version);
                }
                _applied[index] = new() { Entity = edit.Key, Parent = edit.Value, Place = place };
            }
            Difference(edit.Key, default);
            _pending.Remove(edit.Key);
        }
        foreach (var head in _orderedHeads)
        {
            var children = head.Value; children.Written = version;
            _values.Set(head.Key, children);
        }
        _orderedHeads.Clear(); _lastRemovals.Clear();
    }

    public void Detach(TEntity entity)
    {
        RequireActive(entity);
        ulong version = ++Version;
        Main(entity, default, version); Apply(entity, default, version);
        _pending.Remove(entity);
    }

    public void Maintain()
    {
        if (_pending.Count == 0) return;
        var after = _edits; after.Clear();
        ulong version = ++Version;
        foreach (TEntity entity in _pending)
        {
            TEntity parent = Parent(entity);
            if (!Empty(parent) && !Active(parent)) { Main(entity, default, version); parent = default; }
            after.Add(entity, parent);
        }
        ApplyBatch(after, version, false);
        _pending.Clear();
    }

    public void Destroy(TEntity entity)
    {
        RequireActive(entity);
        var children = Children(entity);
        _differences.TryGetValue(entity, out var differences);
        if (children.Count > 0 || differences is { Count: > 0 })
        {
            TEntity last = default;
            var incoming = _incoming;
            incoming.Clear(); incoming.EnsureCapacity(children.Count + (differences?.Count ?? 0));
            incoming.AddRange(children.Items.AsSpan(0, children.Count));
            if (differences is not null) incoming.AddRange(differences);
            foreach (TEntity child in incoming)
            {
                if (Equal(_applied[Prepare(child)].Parent, entity)) last = child;
                TEntity current = Parent(child);
                ulong version = ++Version;
                if (Equal(current, entity)) { Main(child, default, version); current = default; }
                Apply(child, current, version, entity);
                _pending.Remove(child);
            }
            if (children.Count > 0)
            {
                children.Items![0] = last; children.Count = 1;
                _values.Set(entity, children);
                RemoveChildren(entity, ++Version);
            }
        }
        Detach(entity);
        _ordered.Remove(entity);
        _destroyed.Add(entity);
    }

    public void DestroySubtree(TEntity entity)
    {
        RequireActive(entity);
        var selected = _subtree;
        selected.Clear(); selected.Add(entity);
        for (int i = 0; i < selected.Count; i++)
        {
            TEntity parent = selected[i];
            var children = Children(parent);
            foreach (TEntity child in children.Items.AsSpan(0, children.Count))
                if (Active(child) && Equal(Parent(child), parent)) selected.Add(child);
            if (_differences.TryGetValue(parent, out var differences)) selected.AddRange(differences);
        }
        for (int i = selected.Count - 1; i >= 0; i--) Destroy(selected[i]);
    }

    public TEntity Create(int ordinal) => _values.Create(ordinal);

    public void SetOrdered(TEntity parent) => _ordered.Add(parent);
    internal void Reorder(TEntity parent, TEntity first)
    {
        var children = Children(parent);
        int place = Array.IndexOf(children.Items!, first, 0, children.Count);
        children.Items!.AsSpan(0, place).CopyTo(children.Items.AsSpan(1)); children.Items![0] = first;
        for (int i = 0; i <= place; i++) _applied[_values.Index(children.Items[i])].Place = i;
    }

    public void ReadFacts(ulong since, HierarchyObservation<TEntity> result)
    {
        foreach (TEntity entity in _entities)
        {
            if (!_values.Alive(entity)) continue;
            if (_values.Has<ReferenceParent<TEntity>>(entity))
            {
                var parent = _values.Read<ReferenceParent<TEntity>>(entity);
                if (parent.Added > since) result.Fact(entity, 1);
                if (parent.Written > since) result.Fact(entity, 2);
            }
            if (_values.Has<ReferenceChildren<TEntity>>(entity))
            {
                var children = _values.Read<ReferenceChildren<TEntity>>(entity);
                if (children.Added > since) result.Fact(entity, 8);
                if (children.Written > since) result.Fact(entity, 16);
            }
        }
        foreach (TEntity entity in _oldParents)
        {
            var parent = _values.Read<ReferenceOldParent<TEntity>>(entity);
            if (parent.Version > since) result.OldParent(entity, parent.Value);
        }
        foreach (TEntity entity in _oldChildren)
        {
            var children = _values.Read<ReferenceOldChildren<TEntity>>(entity);
            if (children.Version > since) result.OldChildren(entity, children.Items.AsSpan(0, children.Count));
        }
    }

    public void ClearFacts()
    {
        foreach (TEntity entity in _oldParents) _values.Remove<ReferenceOldParent<TEntity>>(entity);
        foreach (TEntity entity in _oldChildren)
        {
            var children = _values.Read<ReferenceOldChildren<TEntity>>(entity);
            ArrayPool<TEntity>.Shared.Return(children.Items!);
            _values.Remove<ReferenceOldChildren<TEntity>>(entity);
        }
        _oldParents.Clear(); _oldChildren.Clear();
        foreach (TEntity entity in _destroyed)
        {
            _applied[_values.Index(entity)] = default;
            _values.Destroy(entity);
        }
        _destroyed.Clear();
    }

    public void Dispose()
    {
        ClearFacts();
        foreach (TEntity entity in _entities)
        {
            var children = Children(entity);
            if (children.Items is { } items) ArrayPool<TEntity>.Shared.Return(items);
        }
        _values.Dispose();
    }
}
