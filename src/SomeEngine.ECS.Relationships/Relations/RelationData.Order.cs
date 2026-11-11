using SomeEngine.ECS.Entities;

namespace SomeEngine.ECS.Relations;

internal sealed partial class RelationData<T>
{
    private readonly Dictionary<AdjacencyKey, List<Entity>> _futureOrders = new();
    private readonly Dictionary<Entity, RelationEntry> _orderEntries = new();
    private readonly List<RelationEntry> _orderValues = new();
    private readonly HashSet<Entity> _orderMembers = new();
    private bool _usesOrder;

    private void ValidateRole(RelationAdjacencyRole role)
    {
        if (_directed ? role == RelationAdjacencyRole.Incident : role != RelationAdjacencyRole.Incident)
            throw new ArgumentException("The adjacency role must match the relation direction.");
    }

    private static bool MainContains(in RelationEndpoints<T> value, AdjacencyKey key)
        => key.Role == RelationAdjacencyRole.Outgoing ? value.Source == key.Entity
            : key.Role == RelationAdjacencyRole.Incoming ? value.Target == key.Entity
            : value.Source == key.Entity || value.Target == key.Entity;

    private List<Entity> Future(AdjacencyKey key)
    {
        if (_futureOrders.TryGetValue(key, out var found)) return found;
        var order = new List<Entity>();
        _orderMembers.Clear();
        foreach (RelationEntry entry in ReadHead(key.Entity, key.Role).Entries)
            if (Active(entry.Edge) && ReadMain(entry.Edge, out var value) && MainContains(value, key))
            {
                order.Add(entry.Edge);
                _orderMembers.Add(entry.Edge);
            }
        void Add(ReadOnlySpan<Entity> edges)
        {
            foreach (Entity edge in edges)
                if (Active(edge) && ReadMain(edge, out var value) && MainContains(value, key) && _orderMembers.Add(edge)) order.Add(edge);
        }
        Add(_sourceDifferences.Members(key.Entity));
        Add(_targetDifferences.Members(key.Entity));
        _futureOrders.Add(key, order);
        return order;
    }

    internal void CollectCurrentEdges(Entity endpoint, RelationAdjacencyRole role, HashSet<Entity> result)
    {
        ValidateRole(role);
        var key = new AdjacencyKey(endpoint, role);
        foreach (RelationEntry entry in ReadHead(endpoint, role).Entries) Add(entry.Edge);
        if (role != RelationAdjacencyRole.Incoming)
            foreach (Entity edge in _sourceDifferences.Members(endpoint)) Add(edge);
        if (role != RelationAdjacencyRole.Outgoing)
            foreach (Entity edge in _targetDifferences.Members(endpoint)) Add(edge);
        void Add(Entity edge)
        {
            if (Active(edge) && ReadMain(edge, out var value) && MainContains(value, key)) result.Add(edge);
        }
    }

    private void ChangeFuture(Entity edge, Entity endpoint, RelationAdjacencyRole role, bool add, bool deferred)
    {
        if (endpoint == Entity.Null) return;
        var key = new AdjacencyKey(endpoint, role);
        if (!_futureOrders.TryGetValue(key, out var order))
        {
            if (!deferred || !ReadHead(endpoint, role).IsOrdered) return;
            order = Future(key);
        }
        order.Remove(edge);
        if (add) order.Add(edge);
    }

    private void UpdateFuture(Entity edge, in RelationEndpoints<T> before, in RelationEndpoints<T> after, bool deferred)
    {
        if (!_usesOrder) return;
        if (_directed)
        {
            if (before.Source != after.Source)
            {
                ChangeFuture(edge, before.Source, RelationAdjacencyRole.Outgoing, false, deferred);
                ChangeFuture(edge, after.Source, RelationAdjacencyRole.Outgoing, true, deferred);
            }
            if (before.Target != after.Target)
            {
                ChangeFuture(edge, before.Target, RelationAdjacencyRole.Incoming, false, deferred);
                ChangeFuture(edge, after.Target, RelationAdjacencyRole.Incoming, true, deferred);
            }
        }
        else
        {
            if (before.Source != after.Source && before.Source != after.Target) ChangeFuture(edge, before.Source, RelationAdjacencyRole.Incident, false, deferred);
            if (before.Target != before.Source && before.Target != after.Source && before.Target != after.Target) ChangeFuture(edge, before.Target, RelationAdjacencyRole.Incident, false, deferred);
            if (after.Source != before.Source && after.Source != before.Target) ChangeFuture(edge, after.Source, RelationAdjacencyRole.Incident, true, deferred);
            if (after.Target != after.Source && after.Target != before.Source && after.Target != before.Target) ChangeFuture(edge, after.Target, RelationAdjacencyRole.Incident, true, deferred);
        }
    }

    private void UpdateBatchFuture(bool deferred)
    {
        var heads = _batchHeadCounts;
        heads.Clear();
        void AddHead(Entity endpoint, RelationAdjacencyRole role)
        {
            if (endpoint == Entity.Null) return;
            var key = new AdjacencyKey(endpoint, role);
            if (_futureOrders.ContainsKey(key) || deferred && ReadHead(endpoint, role).IsOrdered) heads.TryAdd(key, 0);
        }
        foreach (var edit in _batchEdits)
        {
            RelationEndpoints<T> before = _batchBefore[edit.Key], after = edit.Value;
            if (_directed)
            {
                if (before.Source != after.Source)
                { AddHead(before.Source, RelationAdjacencyRole.Outgoing); AddHead(after.Source, RelationAdjacencyRole.Outgoing); }
                if (before.Target != after.Target)
                { AddHead(before.Target, RelationAdjacencyRole.Incoming); AddHead(after.Target, RelationAdjacencyRole.Incoming); }
            }
            else
            {
                if (before.Source != after.Source && before.Source != after.Target) AddHead(before.Source, RelationAdjacencyRole.Incident);
                if (before.Target != before.Source && before.Target != after.Source && before.Target != after.Target) AddHead(before.Target, RelationAdjacencyRole.Incident);
                if (after.Source != before.Source && after.Source != before.Target) AddHead(after.Source, RelationAdjacencyRole.Incident);
                if (after.Target != after.Source && after.Target != before.Source && after.Target != before.Target) AddHead(after.Target, RelationAdjacencyRole.Incident);
            }
        }
        if (heads.Count == 0) return;
        foreach (AdjacencyKey key in heads.Keys)
        {
            List<Entity> order = Future(key);
            int kept = 0;
            for (int i = 0; i < order.Count; i++)
            {
                Entity edge = order[i];
                if (!_batchEdits.TryGetValue(edge, out var after)
                    || MainContains(_batchBefore[edge], key) == MainContains(after, key)) order[kept++] = edge;
            }
            order.RemoveRange(kept, order.Count - kept);
        }
        void Arrive(Entity edge, Entity endpoint, RelationAdjacencyRole role)
        {
            var key = new AdjacencyKey(endpoint, role);
            if (heads.ContainsKey(key)) _futureOrders[key].Add(edge);
        }
        foreach (var edit in _batchEdits)
        {
            RelationEndpoints<T> before = _batchBefore[edit.Key], after = edit.Value;
            if (_directed)
            {
                if (before.Source != after.Source) Arrive(edit.Key, after.Source, RelationAdjacencyRole.Outgoing);
                if (before.Target != after.Target) Arrive(edit.Key, after.Target, RelationAdjacencyRole.Incoming);
            }
            else
            {
                if (after.Source != before.Source && after.Source != before.Target) Arrive(edit.Key, after.Source, RelationAdjacencyRole.Incident);
                if (after.Target != after.Source && after.Target != before.Source && after.Target != before.Target) Arrive(edit.Key, after.Target, RelationAdjacencyRole.Incident);
            }
        }
    }

    internal bool IsOrdered(Entity endpoint, RelationAdjacencyRole role) => ReadHead(endpoint, role).IsOrdered;

    internal void SetOrdered(Entity endpoint, RelationAdjacencyRole role, bool ordered, ReadOnlySpan<Entity> order)
    {
        _world.RequireStructure();
        RequireActive(endpoint);
        ValidateRole(role);
        EnsureCapacity();
        ref AdjacencyHead head = ref PrepareHead(endpoint, role);
        if (!order.IsEmpty)
        {
            if (order.Length != head.Count) throw new ArgumentException("The order must contain every adjacency edge.");
            _orderEntries.Clear(); _orderValues.Clear();
            foreach (RelationEntry entry in head.Entries) _orderEntries.Add(entry.Edge, entry);
            foreach (Entity edge in order)
            {
                if (!_orderEntries.Remove(edge, out var entry)) throw new ArgumentException("The order must contain each adjacency edge once.");
                _orderValues.Add(entry);
            }
            for (int i = 0; i < _orderValues.Count; i++)
            {
                RelationEntry entry = _orderValues[i];
                head.Items![i] = entry;
                WritePlace(entry.Edge, endpoint, role, i);
            }
        }
        head.CountFlags = head.Count | (ordered ? int.MinValue : 0);
        _usesOrder |= ordered;
        var key = new AdjacencyKey(endpoint, role);
        if (!ordered) _futureOrders.Remove(key);
        else if (_futureOrders.TryGetValue(key, out var future))
        {
            _orderMembers.Clear();
            foreach (Entity edge in future) _orderMembers.Add(edge);
            _orderValues.Clear();
            foreach (RelationEntry entry in head.Entries) if (_orderMembers.Remove(entry.Edge)) _orderValues.Add(entry);
            foreach (Entity edge in future) if (_orderMembers.Contains(edge)) _orderValues.Add(new(default, edge));
            future.Clear();
            foreach (RelationEntry entry in _orderValues) future.Add(entry.Edge);
        }
    }

    internal void Reorder(Entity endpoint, RelationAdjacencyRole role, Entity edge, int insertAt)
    {
        _world.RequireStructure();
        RequireActive(endpoint); RequireActive(edge);
        ValidateRole(role);
        EnsureCapacity();
        ref AdjacencyHead head = ref PrepareHead(endpoint, role);
        if (!head.IsOrdered) throw new InvalidOperationException("Enable ordered adjacency before reordering members.");
        AppliedEdge value = Applied(edge);
        int before = role == RelationAdjacencyRole.Outgoing ? value.SourcePlace : role == RelationAdjacencyRole.Incoming ? value.TargetPlace : Place(value, endpoint);
        if (role == RelationAdjacencyRole.Outgoing ? value.Source != endpoint
            : role == RelationAdjacencyRole.Incoming ? value.Target != endpoint : !Contains(value, endpoint))
            throw new ArgumentException("The edge must belong to this adjacency.");
        if ((uint)insertAt >= (uint)head.Count) throw new ArgumentOutOfRangeException(nameof(insertAt));
        if (before == insertAt) { RestoreFuturePlace(endpoint, role, edge); return; }
        RelationEntry entry = head.Items![before];
        if (before < insertAt) Array.Copy(head.Items, before + 1, head.Items, before, insertAt - before);
        else Array.Copy(head.Items, insertAt, head.Items, insertAt + 1, before - insertAt);
        head.Items[insertAt] = entry;
        for (int i = Math.Min(before, insertAt); i <= Math.Max(before, insertAt); i++) WritePlace(head.Items[i].Edge, endpoint, role, i);
        RestoreFuturePlace(endpoint, role, edge);
    }

    private void RestoreFuturePlace(Entity endpoint, RelationAdjacencyRole role, Entity edge)
    {
        if (_futureOrders.TryGetValue(new(endpoint, role), out var future) && future.Remove(edge))
        {
            AdjacencyHead head = ReadHead(endpoint, role);
            int place = future.Count;
            bool found = false;
            for (int i = 0; i < head.Count; i++)
            {
                if (head.Items![i].Edge == edge) { found = true; continue; }
                if (!found) continue;
                int next = future.IndexOf(head.Items[i].Edge);
                if (next >= 0) { place = next; break; }
            }
            future.Insert(place, edge);
        }
    }

    internal void ReorderDeferred(Entity endpoint, RelationAdjacencyRole role, Entity edge, int insertAt)
    {
        _world.RequireStructure();
        RequireActive(endpoint); RequireActive(edge); ValidateRole(role);
        if (!ReadHead(endpoint, role).IsOrdered)
            throw new InvalidOperationException("Enable ordered adjacency before reordering members.");
        List<Entity> order = Future(new(endpoint, role));
        int previous = order.IndexOf(edge);
        if (previous < 0) throw new ArgumentException("The edge must belong to this adjacency.");
        if ((uint)insertAt >= (uint)order.Count) throw new ArgumentOutOfRangeException(nameof(insertAt));
        order.RemoveAt(previous);
        order.Insert(insertAt, edge);
        _hasDeferredChanges = true;
    }

    private void PublishFutureOrders()
    {
        foreach (var pair in _futureOrders)
        {
            if (!Active(pair.Key.Entity)) continue;
            ref AdjacencyHead head = ref HeadRow(pair.Key.Entity, pair.Key.Role);
            for (int i = 0; i < pair.Value.Count; i++)
            {
                Entity edge = pair.Value[i];
                ref AppliedEdge value = ref AppliedRow(edge);
                Entity other = pair.Key.Role == RelationAdjacencyRole.Outgoing ? value.Target
                    : pair.Key.Role == RelationAdjacencyRole.Incoming ? value.Source
                    : value.Source == pair.Key.Entity ? value.Target : value.Source;
                head.Items![i] = new(other, edge);
                WritePlace(ref value, pair.Key.Entity, pair.Key.Role, i);
            }
            head.SetCount(pair.Value.Count);
            if (head.Count == 0 && head.Items is { } items)
            {
                AdjacencyHead.Return(items);
                head.Items = null;
            }
        }
        _futureOrders.Clear();
    }

    private void ClearOrder()
    {
        _futureOrders.Clear(); _orderEntries.Clear(); _orderValues.Clear(); _orderMembers.Clear();
        _usesOrder = false;
    }
}
