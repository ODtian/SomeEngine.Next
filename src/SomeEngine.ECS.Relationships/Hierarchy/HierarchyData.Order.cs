using SomeEngine.ECS.Entities;
using System.Runtime.InteropServices;

namespace SomeEngine.ECS.Hierarchy;

internal sealed partial class HierarchyData<TDomain>
{
    private readonly Stack<List<Entity>> _spareChildOrders = new();

    private List<Entity> CopyChildOrder(ReadOnlySpan<Entity> children)
    {
        List<Entity> order = _spareChildOrders.TryPop(out var spare) ? spare : new(children.Length);
        order.AddRange(children);
        return order;
    }

    private void ReleaseChildOrder(List<Entity> order)
    {
        order.Clear();
        _spareChildOrders.Push(order);
    }

    private void ReleaseProjected(Entity parent)
    {
        if (_projected.Remove(parent, out var order)) ReleaseChildOrder(order);
    }

    private void ReplaceProjected(Entity parent, List<Entity> order)
    {
        ReleaseProjected(parent);
        _projected[parent] = order;
    }

    private void ClearProjected()
    {
        foreach (List<Entity> order in _projected.Values) ReleaseChildOrder(order);
        _projected.Clear();
    }

    internal bool IsOrdered(Entity parent)
    {
        Children<TDomain> children = ReadChildren(parent);
        return children.Items is not null ? children.IsOrdered : _emptyOrdered.Contains(parent);
    }

    private List<Entity> CurrentMembers(Entity parent)
    {
        Children<TDomain> children = ReadChildren(parent);
        var members = CopyChildOrder([]);
        if (!_hasDeferredChanges) { members.AddRange(children.Entities); return members; }
        foreach (Entity child in children.Entities)
            if (Active(child) && ReadParent(child) == parent) members.Add(child);
        foreach (Entity child in _differences.Members(parent))
            if (Active(child)) members.Add(child);
        return members;
    }

    private List<Entity> Projected(Entity parent)
    {
        if (_projected.TryGetValue(parent, out var found)) return found;
        List<Entity> members = CurrentMembers(parent);
        _projected.Add(parent, members);
        return members;
    }

    private void ValidateInsertion(Entity child, Entity parent, bool deferred, int insertAt)
    {
        if (parent == Entity.Null || insertAt < 0) return;
        if (!IsOrdered(parent)) throw new ArgumentException("Insertion positions require ordered children.");
        int count = deferred ? Projected(parent).Count : ReadChildren(parent).Count;
        if (deferred ? Projected(parent).Contains(child) : Applied(child).Parent == parent) count--;
        if ((uint)insertAt > (uint)count) throw new ArgumentOutOfRangeException(nameof(insertAt));
    }

    private int ProjectedInsertion(Entity parent, Entity child, List<Entity> projected, int insertAt)
    {
        if (insertAt < 0) return projected.Count;
        int position = 0;
        foreach (Entity member in ReadChildren(parent).Entities)
        {
            if (member == child) continue;
            if (position++ < insertAt) continue;
            int index = projected.IndexOf(member);
            if (index >= 0) return index;
        }
        return projected.Count;
    }

    private void UpdateProjected(Entity child, Entity before, Entity after, bool deferred, int insertAt)
    {
        if (before == after && insertAt < 0) return;
        List<Entity>? target = null;
        if (after != Entity.Null && IsOrdered(after))
        {
            if (deferred) target = Projected(after);
            else _projected.TryGetValue(after, out target);
        }
        if (before != Entity.Null && _projected.TryGetValue(before, out var source)) source.Remove(child);
        if (target is null) return;
        target.Remove(child);
        int position = deferred ? (insertAt < 0 ? target.Count : insertAt) : ProjectedInsertion(after, child, target, insertAt);
        target.Insert(position, child);
    }

    internal void SetOrdered(Entity parent, bool ordered, ReadOnlySpan<Entity> order)
    {
        _world.RequireStructure();
        RequireActive(_world, parent);
        if (!_hasDeferredChanges) { SetChildrenOrder(parent, ordered, order); return; }
        List<Entity> future = _projected.TryGetValue(parent, out var projected) ? CopyChildOrder(CollectionsMarshal.AsSpan(projected)) : CurrentMembers(parent);
        if (!order.IsEmpty)
        {
            var members = new HashSet<Entity>(future);
            if (order.Length != members.Count) throw new ArgumentException("Order must contain every current child once.");
            foreach (Entity child in order)
                if (!members.Remove(child)) throw new ArgumentException("Order must contain every current child once.");
            future.Clear(); future.AddRange(order);
        }
        _usesChildOrder |= ordered;
        Children<TDomain> children = ReadChildren(parent);
        if (children.Count == 0)
        {
            if (ordered) _emptyOrdered.Add(parent); else _emptyOrdered.Remove(parent);
        }
        else
        {
            var applied = new List<Entity>(children.Count);
            foreach (Entity child in future) if (Applied(child).Parent == parent) applied.Add(child);
            var present = new HashSet<Entity>(applied);
            foreach (Entity child in children.Entities) if (present.Add(child)) applied.Add(child);
            WriteOrder(parent, applied, ordered);
        }
        if (!ordered) { ReleaseProjected(parent); ReleaseChildOrder(future); }
        else
        {
            bool differs = future.Count != children.Count;
            foreach (Entity child in children.Entities) differs |= ReadParent(child) != parent;
            if (differs) ReplaceProjected(parent, future);
            else ReleaseChildOrder(future);
        }
    }

    private void SetChildrenOrder(Entity parent, bool ordered, ReadOnlySpan<Entity> order)
    {
        Children<TDomain> children = ReadChildren(parent);
        if (!order.IsEmpty)
        {
            if (order.Length != children.Count) throw new ArgumentException("Order must contain every current child once.");
            var members = _batchSeen; members.Clear();
            foreach (Entity child in children.Entities) members.Add(child, 0);
            foreach (Entity child in order)
                if (!members.Remove(child)) throw new ArgumentException("Order must contain every current child once.");
            for (int i = 0; i < order.Length; i++)
            {
                children.Items![i] = order[i];
                WritePlace(order[i], i);
            }
        }
        _usesChildOrder |= ordered;
        if (children.Count == 0)
        {
            if (ordered) _emptyOrdered.Add(parent); else _emptyOrdered.Remove(parent);
            return;
        }
        ChildrenRow(parent, out var chunk, out int row) = new(children.Items, children.Count, ordered);
        World.Stamp<Children<TDomain>>(chunk, row, _world.NextVersion());
    }

    private void WriteOrder(Entity parent, List<Entity> order, bool ordered, ulong version = 0)
    {
        ref Children<TDomain> children = ref ChildrenRow(parent, out var chunk, out int row);
        CollectionsMarshal.AsSpan(order).CopyTo(children.Items);
        for (int i = 0; i < order.Count; i++)
        {
            WritePlace(order[i], i);
        }
        children = new(children.Items, order.Count, ordered);
        World.Stamp<Children<TDomain>>(chunk, row, version != 0 ? version : _world.NextVersion());
    }
}
