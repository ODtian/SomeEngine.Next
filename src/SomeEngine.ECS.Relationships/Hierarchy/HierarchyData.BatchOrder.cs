using SomeEngine.ECS.Entities;
using System.Runtime.InteropServices;

namespace SomeEngine.ECS.Hierarchy;

internal sealed partial class HierarchyData<TDomain>
{
    private readonly Dictionary<Entity, List<Entity>> _batchAppliedOrders = new(), _batchFutureOrders = new();
    private readonly Dictionary<Entity, Entity> _batchMainParents = new(), _batchAppliedParents = new();
    private readonly Dictionary<Entity, int> _batchMainArrivals = new(), _batchAppliedArrivals = new();
    private readonly List<List<Entity>> _batchChildren = new();
    private int _batchChildrenCount;
    private bool _usesChildOrder;

    private List<Entity> BatchChildren(ReadOnlySpan<Entity> children)
    {
        if (_batchChildrenCount == _batchChildren.Count) _batchChildren.Add(new(children.Length));
        List<Entity> members = _batchChildren[_batchChildrenCount++];
        members.Clear(); members.AddRange(children);
        return members;
    }

    private (Dictionary<Entity, List<Entity>> Applied, Dictionary<Entity, List<Entity>> Future) BatchOrders(
        ReadOnlySpan<ParentEdit> edits, bool deferred, out bool direct)
    {
        direct = false;
        var applied = _batchAppliedOrders;
        var future = _batchFutureOrders;
        applied.Clear(); future.Clear();
        _batchChildrenCount = 0;
        if (!_usesChildOrder)
        {
            foreach (ParentEdit edit in edits)
                if (edit.InsertAt >= 0) throw new ArgumentException("Insertion positions require ordered children.");
            return (applied, future);
        }
        bool append = true;
        foreach (ParentEdit edit in edits) append &= edit.InsertAt < 0;
        direct = append && !deferred && !_hasDeferredChanges && _projected.Count == 0 && _batchEdits.Count == edits.Length;
        if (direct) return (applied, future);
        if (append) return BatchAppendOrders(edits, deferred);
        var mainParents = _batchMainParents;
        var appliedParents = _batchAppliedParents;
        mainParents.Clear(); appliedParents.Clear();
        bool Ordered(Entity parent) => _retiredChildren.TryGetValue(parent, out var retired) ? retired.IsOrdered : IsOrdered(parent);
        List<Entity> Actual(Entity parent)
        {
            if (applied.TryGetValue(parent, out var found)) return found;
            Children<TDomain> value = _retiredChildren.TryGetValue(parent, out var retired) ? retired : ReadChildren(parent);
            List<Entity> members = BatchChildren(value.Entities);
            applied.Add(parent, members);
            return members;
        }
        List<Entity> Future(Entity parent)
        {
            if (future.TryGetValue(parent, out var found)) return found;
            var members = _projected.TryGetValue(parent, out var projected) ? CopyChildOrder(CollectionsMarshal.AsSpan(projected)) : CurrentMembers(parent);
            future.Add(parent, members);
            return members;
        }
        foreach (ParentEdit edit in edits)
        {
            Entity mainBefore = mainParents.TryGetValue(edit.Child, out var previousMain) ? previousMain : ReadParentKnown(edit.Child);
            Entity appliedBefore = appliedParents.TryGetValue(edit.Child, out var previousApplied) ? previousApplied : Applied(edit.Child).Parent;
            bool futureChanges = mainBefore != edit.Parent || edit.InsertAt >= 0;
            bool appliedChanges = !deferred && (appliedBefore != edit.Parent || edit.InsertAt >= 0);
            if (edit.Parent != Entity.Null && edit.InsertAt >= 0 && !Ordered(edit.Parent))
                throw new ArgumentException("Insertion positions require ordered children.");
            if (futureChanges && mainBefore != Entity.Null && Ordered(mainBefore)) Future(mainBefore).Remove(edit.Child);
            if (appliedChanges && appliedBefore != Entity.Null && Ordered(appliedBefore)) Actual(appliedBefore).Remove(edit.Child);
            List<Entity>? actualTarget = appliedChanges && edit.Parent != Entity.Null && Ordered(edit.Parent) ? Actual(edit.Parent) : null;
            int actualIndex = actualTarget is null ? -1 : (edit.InsertAt < 0 ? actualTarget.Count : edit.InsertAt);
            if (actualTarget is not null && (uint)actualIndex > (uint)actualTarget.Count)
                throw new ArgumentOutOfRangeException(nameof(edits));
            if (futureChanges && edit.Parent != Entity.Null && Ordered(edit.Parent))
            {
                List<Entity> target = Future(edit.Parent);
                target.Remove(edit.Child);
                int index = target.Count;
                if (deferred)
                {
                    index = edit.InsertAt < 0 ? target.Count : edit.InsertAt;
                    if ((uint)index > (uint)target.Count) throw new ArgumentOutOfRangeException(nameof(edits));
                }
                else if (edit.InsertAt >= 0 && actualTarget is not null)
                    for (int i = actualIndex; i < actualTarget.Count; i++)
                    {
                        int anchor = target.IndexOf(actualTarget[i]);
                        if (anchor < 0) continue;
                        index = anchor;
                        break;
                    }
                target.Insert(index, edit.Child);
            }
            actualTarget?.Insert(actualIndex, edit.Child);
            mainParents[edit.Child] = edit.Parent;
            if (!deferred) appliedParents[edit.Child] = edit.Parent;
        }
        return (applied, future);
    }

    private (Dictionary<Entity, List<Entity>> Applied, Dictionary<Entity, List<Entity>> Future) BatchAppendOrders(
        ReadOnlySpan<ParentEdit> edits, bool deferred)
    {
        var applied = _batchAppliedOrders; var future = _batchFutureOrders;
        var mainParents = _batchMainParents; var appliedParents = _batchAppliedParents;
        var mainArrivals = _batchMainArrivals; var appliedArrivals = _batchAppliedArrivals;
        mainParents.Clear(); appliedParents.Clear(); mainArrivals.Clear(); appliedArrivals.Clear();
        bool unique = _batchEdits.Count == edits.Length;
        bool hasProjected = _projected.Count != 0;
        bool mainIsApplied = !_hasDeferredChanges;
        bool Ordered(Entity parent) => parent != Entity.Null
            && (_retiredChildren.TryGetValue(parent, out var retired) ? retired.IsOrdered : IsOrdered(parent));
        void Actual(Entity parent)
        {
            if (applied.ContainsKey(parent) || !Ordered(parent)) return;
            Children<TDomain> children = _retiredChildren.TryGetValue(parent, out var retired) ? retired : ReadChildren(parent);
            List<Entity> members = BatchChildren(children.Entities); applied.Add(parent, members);
        }
        void Future(Entity parent)
        {
            if (!deferred && (!hasProjected || !_projected.ContainsKey(parent))) return;
            if (future.ContainsKey(parent) || !Ordered(parent)) return;
            future.Add(parent, _projected.TryGetValue(parent, out var projected) ? CopyChildOrder(CollectionsMarshal.AsSpan(projected)) : CurrentMembers(parent));
        }
        for (int index = 0; index < edits.Length; index++)
        {
            ParentEdit edit = edits[index];
            Entity before = !unique && mainParents.TryGetValue(edit.Child, out var main) ? main : ReadParentKnown(edit.Child);
            Entity mainBefore = before;
            if (before != edit.Parent)
            { Future(before); Future(edit.Parent); if (!unique) mainArrivals[edit.Child] = index; }
            if (!unique) mainParents[edit.Child] = edit.Parent;
            if (deferred) continue;
            before = mainIsApplied ? mainBefore
                : !unique && appliedParents.TryGetValue(edit.Child, out var current) ? current : Applied(edit.Child).Parent;
            if (before != edit.Parent)
            { Actual(before); Actual(edit.Parent); if (!unique) appliedArrivals[edit.Child] = index; }
            if (!unique && !mainIsApplied) appliedParents[edit.Child] = edit.Parent;
        }
        void Compact(Dictionary<Entity, List<Entity>> heads, Dictionary<Entity, int> moved)
        {
            foreach (var head in heads)
            {
                List<Entity> children = head.Value;
                int kept = 0;
                for (int i = 0; i < children.Count; i++)
                {
                    Entity child = children[i];
                    bool changed = unique ? _batchEdits.TryGetValue(child, out var parent)
                        && head.Key != parent : moved.ContainsKey(child);
                    if (!changed) children[kept++] = child;
                }
                children.RemoveRange(kept, children.Count - kept);
            }
        }
        Compact(applied, appliedArrivals); Compact(future, mainArrivals);
        for (int index = 0; index < edits.Length; index++)
        {
            ParentEdit edit = edits[index];
            bool mainMoved = unique ? ReadParentKnown(edit.Child) != edit.Parent : mainArrivals.TryGetValue(edit.Child, out int main) && main == index;
            if (mainMoved
                && future.TryGetValue(edit.Parent, out var projected)) projected.Add(edit.Child);
            bool appliedMoved = !deferred && (mainIsApplied ? mainMoved
                : unique ? Applied(edit.Child).Parent != edit.Parent
                : appliedArrivals.TryGetValue(edit.Child, out int actual) && actual == index);
            if (appliedMoved
                && applied.TryGetValue(edit.Parent, out var children)) children.Add(edit.Child);
        }
        return (applied, future);
    }
}
