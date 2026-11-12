using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Storage;

namespace SomeEngine.ECS.Hierarchy;

internal sealed partial class HierarchyData<TDomain>
{
    private struct HeadChange(int count, bool retired, bool ordered)
    {
        internal readonly bool Retired = retired, Ordered = ordered;
        internal bool Removed;
        internal int AfterCount = count;
    }

    private readonly Dictionary<Entity, Entity> _batchEdits = new();
    private readonly Dictionary<Entity, int> _batchSeen = new(), _batchColumns = new();
    private readonly Dictionary<Entity, HeadChange> _batchHeads = new();
    private readonly Dictionary<Entity, Children<TDomain>> _batchOrderedChildren = new();
    private readonly Dictionary<Archetype, int> _batchTargets = new();
    private readonly Dictionary<(Chunk Chunk, int Flags), int> _batchSources = new();

    private static readonly TypeSet[] BatchColumns = BuildBatchColumns();
    private static TypeSet[] BuildBatchColumns()
    {
        int[] types = [ComponentType<Parent<TDomain>>.TypeId, ComponentType<Removed<Parent<TDomain>>>.TypeId,
            ComponentType<Children<TDomain>>.TypeId, ComponentType<Removed<Children<TDomain>>>.TypeId];
        var combinations = new TypeSet[16];
        for (int flags = 0; flags < combinations.Length; flags++)
        {
            int retained = flags | ((flags & 1) << 1) | ((flags & 4) << 1);
            int[] columns = types.Where((_, bit) => (retained & (1 << bit)) != 0).ToArray();
            // Declared lifecycle columns share the topology row's first layout transition.
            combinations[flags] = new TypeSet(false, flags == 0 ? columns : [.. columns, ComponentType<Destroyed>.TypeId]);
        }
        return combinations;
    }

    internal void SetParents(ReadOnlySpan<ParentEdit> edits, bool deferred)
    {
        _world.RequireStructure();
        EnsureCapacity();
        var final = _batchEdits;
        final.Clear(); final.EnsureCapacity(edits.Length);
        Entity checkedParent = Entity.Null;
        foreach (ParentEdit edit in edits)
        {
            RequireActive(_world, edit.Child);
            if (edit.Parent != Entity.Null && edit.Parent != checkedParent)
            {
                RequireActive(_world, edit.Parent);
                checkedParent = edit.Parent;
            }
            final[edit.Child] = edit.Parent;
        }
        if (final.Count == 0) return;
        ValidateFinalForest(final);
        var (appliedOrders, futureOrders) = BatchOrders(edits, deferred, out bool directOrders);
        _batchOrderedChildren.Clear();
        var heads = _batchHeads;
        heads.Clear();
        ref HeadChange Head(Entity parent)
        {
            ref HeadChange head = ref CollectionsMarshal.GetValueRefOrAddDefault(heads, parent, out bool exists);
            if (!exists)
            {
                bool retired = _retiredChildren.TryGetValue(parent, out var value);
                if (!retired) value = ReadChildren(parent);
                head = new(value.Count, retired, value.Items is not null ? value.IsOrdered : _emptyOrdered.Contains(parent));
            }
            return ref head;
        }
        if (!deferred)
            foreach (var edit in final)
            {
                Entity before = _hasDeferredChanges ? Applied(edit.Key).Parent : ReadParentKnown(edit.Key);
                if (before == edit.Value) continue;
                if (before != Entity.Null)
                { ref HeadChange previous = ref Head(before); previous.AfterCount--; previous.Removed = true; }
                if (edit.Value != Entity.Null) Head(edit.Value).AfterCount++;
            }
        var columns = _batchColumns;
        columns.Clear();
        void RequireColumns(Entity entity, int flags)
        {
            if (_world.Records[entity.Id].Chunk!.Archetype.Types.Bits.Contains(BatchColumns[flags].Bits)) return;
            columns[entity] = columns.GetValueOrDefault(entity) | flags;
        }
        foreach (var edit in final)
        {
            PrepareAppliedKnown(edit.Key);
            if (edit.Value != Entity.Null) RequireColumns(edit.Key, 1);
            else if (ReadParentKnown(edit.Key) != Entity.Null) RequireColumns(edit.Key, 2);
        }
        foreach (var pair in heads)
        {
            if (pair.Value.Retired) continue;
            RequireColumns(pair.Key, pair.Value.AfterCount > 0 ? 4 : 8);
        }
        var targets = _batchTargets;
        targets.Clear();
        var sources = _batchSources; sources.Clear();
        foreach (var pair in columns)
        {
            Chunk chunk = _world.Records[pair.Key.Id].Chunk!;
            CollectionsMarshal.GetValueRefOrAddDefault(sources, (chunk, pair.Value), out _)++;
        }
        foreach (var pair in sources)
        {
            Chunk chunk = pair.Key.Chunk;
            Archetype target = _world.ColumnsTarget(chunk.Entities[0], BatchColumns[pair.Key.Flags]);
            if (target != chunk.Archetype) targets[target] = targets.GetValueOrDefault(target) + pair.Value;
        }
        foreach (var pair in targets)
        {
            int count = 0;
            foreach (Chunk chunk in pair.Key.AllChunks) count += chunk.Count;
            pair.Key.Reserve(count + pair.Value);
        }
        if (_world.ChunkCapacity == 0)
            foreach (var pair in sources)
                if (pair.Value == pair.Key.Chunk.Count)
                    _world.EnsureColumns(pair.Key.Chunk, BatchColumns[pair.Key.Flags]);
        foreach (var pair in columns) _world.EnsureColumns(pair.Key, BatchColumns[pair.Value]);
        ulong version = _world.NextVersion();
        foreach (var pair in heads)
        {
            HeadChange head = pair.Value;
            if (head.Retired || head.AfterCount == 0) continue;
            Children<TDomain> current = ReadChildren(pair.Key);
            if (current.Items is null)
            {
                ref EntityRecord record = ref _world.Resolve(pair.Key);
                _world.WriteColumn(pair.Key, ref record,
                    new Children<TDomain>(Children<TDomain>.Rent(head.AfterCount), 0, head.Ordered), version);
                _emptyOrdered.Remove(pair.Key);
            }
            else if (current.Items.Length < head.AfterCount)
            {
                Entity[] items = Children<TDomain>.Rent(head.AfterCount);
                current.Entities.CopyTo(items);
                Children<TDomain>.Return(current.Items);
                ChildrenRow(pair.Key, out _, out _) = new(items, current.Count, current.IsOrdered);
            }
        }
        foreach (var edit in final)
        {
            if (edit.Value == Entity.Null) _world.RemoveValue<Parent<TDomain>>(edit.Key, version);
            else
            {
                ref EntityRecord record = ref _world.Resolve(edit.Key);
                _world.WriteColumn(edit.Key, ref record, new Parent<TDomain>(edit.Value), version);
            }
        }
        if (deferred)
            foreach (var edit in final)
                _differences.Set(edit.Key, edit.Value == Applied(edit.Key).Parent ? Entity.Null : edit.Value);
        else
        {
            if (directOrders) PrepareOrderedChildren(heads, final);
            foreach (var edit in final)
            {
                AppliedParent before = Applied(edit.Key);
                if (before.Parent != Entity.Null && before.Parent != edit.Value && heads[before.Parent].AfterCount > 0
                    && !appliedOrders.ContainsKey(before.Parent) && !_batchOrderedChildren.ContainsKey(before.Parent))
                    Drop(before.Parent, edit.Key, before.Place, true, version);
            }
            foreach (var pair in heads)
            {
                HeadChange head = pair.Value;
                if (head.AfterCount != 0) continue;
                if (head.Retired)
                {
                    if (_retiredChildren.Remove(pair.Key, out var removed)) Children<TDomain>.Release(ref removed);
                }
                else
                {
                    if (head.Ordered) _emptyOrdered.Add(pair.Key);
                    _world.RemoveValue<Children<TDomain>>(pair.Key, version);
                }
            }
            foreach (var edit in final)
            {
                AppliedParent before = Applied(edit.Key);
                if (before.Parent != edit.Value)
                {
                    int place = 0;
                    if (edit.Value != Entity.Null && (directOrders || !appliedOrders.ContainsKey(edit.Value)))
                    {
                        ref Children<TDomain> children = ref CollectionsMarshal.GetValueRefOrNullRef(_batchOrderedChildren, edit.Value);
                        if (Unsafe.IsNullRef(ref children)) place = Append(edit.Value, edit.Key, -1, version);
                        else
                        {
                            place = children.Count;
                            children.Items![place] = edit.Key;
                            children = new(children.Items, place + 1, true);
                        }
                    }
                    WriteApplied(edit.Key, new() { Parent = edit.Value, Place = place, Generation = edit.Key.Generation });
                }
                _differences.Remove(edit.Key);
            }
            foreach (var pair in appliedOrders)
                if (heads.TryGetValue(pair.Key, out var head) && head.AfterCount == 0) continue;
                else PublishBatchOrder(pair.Key, pair.Value, version);
            foreach (var pair in _batchOrderedChildren)
            {
                if (heads[pair.Key].Retired) _retiredChildren[pair.Key] = pair.Value;
                else
                {
                    ChildrenRow(pair.Key, out Chunk chunk, out int row) = pair.Value;
                    World.Stamp<Children<TDomain>>(chunk, row, version);
                }
            }
            _batchOrderedChildren.Clear();
        }
        foreach (var pair in futureOrders)
            if (deferred || _projected.ContainsKey(pair.Key)) ReplaceProjected(pair.Key, pair.Value);
            else ReleaseChildOrder(pair.Value);
        if (deferred) _hasDeferredChanges = true;
        else ConfirmAppliedParents();
    }

    private void PrepareOrderedChildren(Dictionary<Entity, HeadChange> heads, Dictionary<Entity, Entity> final)
    {
        foreach (var pair in heads)
        {
            HeadChange head = pair.Value;
            if (!head.Ordered || head.AfterCount == 0) continue;
            Children<TDomain> children = head.Retired ? _retiredChildren[pair.Key] : ReadChildren(pair.Key);
            if (head.Removed)
            {
                int kept = 0;
                foreach (Entity child in children.Entities)
                {
                    if (final.TryGetValue(child, out var parent) && parent != pair.Key) continue;
                    children.Items![kept] = child; WritePlace(child, kept++);
                }
                children = new(children.Items, kept, true);
            }
            _batchOrderedChildren.Add(pair.Key, children);
        }
    }

    private void ValidateFinalForest(Dictionary<Entity, Entity> final)
    {
        var seen = _batchSeen;
        seen.Clear();
        int start = 0;
        foreach (Entity child in final.Keys)
        {
            start++;
            for (Entity entity = child; entity != Entity.Null && Active(entity);)
            {
                if (seen.TryGetValue(entity, out int previous))
                {
                    if (previous == start) throw new ArgumentException("The final parent links would form a cycle.");
                    break;
                }
                seen.Add(entity, start);
                entity = final.TryGetValue(entity, out var parent) ? parent : ReadParentKnown(entity);
            }
        }
    }

    private void PublishBatchOrder(Entity parent, List<Entity> order, ulong version)
    {
        if (_retiredChildren.TryGetValue(parent, out var retired))
        {
            for (int i = 0; i < order.Count; i++) { retired.Items![i] = order[i]; WritePlace(order[i], i); }
            _retiredChildren[parent] = new(retired.Items, order.Count, true);
        }
        else if (ReadChildren(parent).Items is not null) WriteOrder(parent, order, true, version);
    }
}
