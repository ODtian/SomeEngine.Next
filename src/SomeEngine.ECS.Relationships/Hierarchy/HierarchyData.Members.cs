using System.Buffers;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Storage;

namespace SomeEngine.ECS.Hierarchy;

internal sealed partial class HierarchyData<TDomain>
{
    private ref Children<TDomain> ChildrenRow(Entity parent, out Chunk chunk, out int row)
    {
        ref EntityRecord record = ref _world.Resolve(parent);
        chunk = record.Chunk!;
        row = record.Row;
        return ref ColumnEntry.At<Children<TDomain>>(ref chunk.Columns[ComponentType<Children<TDomain>>.TypeId], row);
    }

    private void EnsureChildrenCapacity(Entity parent)
        => EnsureChildrenCapacity(parent, ReadChildren(parent).Count + 1);

    private void EnsureChildrenCapacity(Entity parent, int needed)
    {
        Children<TDomain> children = ReadChildren(parent);
        if (children.Items is null)
        {
            ref EntityRecord record = ref _world.Resolve(parent);
            _world.SetValueKnown(parent, ref record,
                new Children<TDomain>(Children<TDomain>.Rent(needed), 0, _emptyOrdered.Remove(parent)));
            return;
        }
        if (needed <= children.Items!.Length) return;
        Entity[] items = Children<TDomain>.Rent(Math.Max(needed, children.Count * 2));
        children.Entities.CopyTo(items);
        Children<TDomain>.Return(children.Items);
        ChildrenRow(parent, out _, out _) = new(items, children.Count, children.IsOrdered);
    }

    private int Append(Entity parent, Entity child, int insertAt, ulong version = 0)
    {
        ref Children<TDomain> children = ref ChildrenRow(parent, out Chunk chunk, out int row);
        int place = children.IsOrdered && insertAt >= 0 ? insertAt : children.Count;
        if (place < children.Count)
        {
            Array.Copy(children.Items!, place, children.Items!, place + 1, children.Count - place);
            for (int i = place + 1; i <= children.Count; i++) WritePlace(children.Items![i], i);
        }
        children.Items![place] = child;
        children = new(children.Items, children.Count + 1, children.IsOrdered);
        World.Stamp<Children<TDomain>>(chunk, row, version != 0 ? version : _world.NextVersion());
        return place;
    }

    private void Drop(Entity parent, Entity child, int place, bool keepEmpty = false, ulong version = 0)
    {
        if (_retiredChildren.TryGetValue(parent, out var retired))
        {
            DropItems(ref retired, place);
            if (retired.Count == 0)
            {
                Children<TDomain>.Release(ref retired);
                _retiredChildren.Remove(parent);
            }
            else _retiredChildren[parent] = retired;
            return;
        }
        ref Children<TDomain> children = ref ChildrenRow(parent, out Chunk chunk, out int row);
        if (children.Count == 1 && !keepEmpty)
        {
            if (children.IsOrdered) _emptyOrdered.Add(parent);
            _world.RemoveValue<Children<TDomain>>(parent, version);
            return;
        }
        DropItems(ref children, place);
        World.Stamp<Children<TDomain>>(chunk, row, version != 0 ? version : _world.NextVersion());
    }

    private void DropItems(ref Children<TDomain> children, int place)
    {
        int last = children.Count - 1;
        if (place != last)
        {
            if (children.IsOrdered)
            {
                Array.Copy(children.Items!, place + 1, children.Items!, place, last - place);
                for (int i = place; i < last; i++) WritePlace(children.Items![i], i);
            }
            else
            {
                Entity moved = children.Items![last];
                children.Items[place] = moved;
                WritePlace(moved, place);
            }
        }
        children = new(children.Items, last, children.IsOrdered);
    }

    private void MoveWithinParent(Entity parent, Entity child, int oldPlace, int place)
    {
        if (oldPlace == place) return;
        ref Children<TDomain> children = ref ChildrenRow(parent, out Chunk chunk, out int row);
        if (place > oldPlace) Array.Copy(children.Items!, oldPlace + 1, children.Items!, oldPlace, place - oldPlace);
        else Array.Copy(children.Items!, place, children.Items!, place + 1, oldPlace - place);
        children.Items![place] = child;
        for (int i = Math.Min(place, oldPlace); i <= Math.Max(place, oldPlace); i++) WritePlace(children.Items[i], i);
        World.Stamp<Children<TDomain>>(chunk, row, _world.NextVersion());
    }

    private void Apply(Entity child, Entity parent, int insertAt, bool groupedOrder = false, Entity emptyingParent = default)
    {
        AppliedParent before = Applied(child);
        if (before.Parent == parent)
        {
            if (parent != Entity.Null && insertAt >= 0 && IsOrdered(parent)) MoveWithinParent(parent, child, before.Place, insertAt);
            _differences.Remove(child);
            return;
        }
        bool groupedTarget = groupedOrder && _projected.ContainsKey(parent);
        if (parent != Entity.Null && !groupedTarget) EnsureChildrenCapacity(parent);
        if (before.Parent != Entity.Null && before.Parent != emptyingParent
            && !(groupedOrder && _projected.ContainsKey(before.Parent))) Drop(before.Parent, child, before.Place);
        int place = parent == Entity.Null || groupedTarget ? 0 : Append(parent, child, insertAt);
        if (parent == Entity.Null && _applied[child.Id].Generation != child.Generation) _retiredApplied.Remove(child);
        else WriteApplied(child, new() { Parent = parent, Place = place, Generation = child.Generation });
        _differences.Remove(child);
    }
}
