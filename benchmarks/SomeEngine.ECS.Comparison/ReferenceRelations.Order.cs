using System.Buffers;
using System.Runtime.CompilerServices;
using SomeEngine.ECS.Relations;

namespace SomeEngine.ECS.Comparison;

internal sealed partial class ReferenceRelations<TEntity, TIdentity, TValues, TPayload>
    where TEntity : struct where TIdentity : struct, IEquatable<TIdentity> where TValues : struct, IRelationValues<TEntity, TIdentity, TPayload>
    where TPayload : struct, IRelationPayload<TPayload>
{
    private readonly HashSet<HeadKey> _batchOrderHeads = new();

    private bool Contains(TEntity source, TEntity target, HeadKey key)
        => _directed ? Equal(key.Incoming ? target : source, key.Entity) : Equal(source, key.Entity) || Equal(target, key.Entity);

    private bool UsesFutureOrder(TEntity endpoint, bool incoming, bool groupedOrder)
        => groupedOrder && _futureOrders.ContainsKey(new(endpoint, incoming));

    private void UpdateBatchFuture(bool deferred)
    {
        var heads = _batchOrderHeads;
        heads.Clear();
        void AddHead(TEntity endpoint, bool incoming)
        {
            if (Empty(endpoint)) return;
            var key = new HeadKey(endpoint, incoming);
            if (_futureOrders.ContainsKey(key) || deferred && ReadHead(endpoint, incoming).Ordered) heads.Add(key);
        }
        foreach (var edit in _edits.Values)
        {
            var before = _values.Read<ReferenceRelationEndpoints<TEntity>>(edit.Edge);
            if (_directed)
            {
                if (!Equal(before.Source, edit.Source)) { AddHead(before.Source, false); AddHead(edit.Source, false); }
                if (!Equal(before.Target, edit.Target)) { AddHead(before.Target, true); AddHead(edit.Target, true); }
            }
            else
            {
                if (!Equal(before.Source, edit.Source) && !Equal(before.Source, edit.Target)) AddHead(before.Source, false);
                if (!Equal(before.Target, before.Source) && !Equal(before.Target, edit.Source) && !Equal(before.Target, edit.Target)) AddHead(before.Target, false);
                if (!Equal(edit.Source, before.Source) && !Equal(edit.Source, before.Target)) AddHead(edit.Source, false);
                if (!Equal(edit.Target, edit.Source) && !Equal(edit.Target, before.Source) && !Equal(edit.Target, before.Target)) AddHead(edit.Target, false);
            }
        }
        if (heads.Count == 0) return;
        foreach (HeadKey key in heads)
        {
            List<TEntity> order = Future(key);
            int kept = 0;
            for (int i = 0; i < order.Count; i++)
            {
                TEntity edge = order[i];
                if (!_edits.TryGetValue(edge, out var edit)
                    || Contains(_values.Read<ReferenceRelationEndpoints<TEntity>>(edge), key) == Contains(edit.Source, edit.Target, key))
                    order[kept++] = edge;
            }
            order.RemoveRange(kept, order.Count - kept);
        }
        void Arrive(TEntity edge, TEntity endpoint, bool incoming)
        {
            var key = new HeadKey(endpoint, incoming);
            if (heads.Contains(key)) _futureOrders[key].Add(edge);
        }
        foreach (var edit in _edits.Values)
        {
            var before = _values.Read<ReferenceRelationEndpoints<TEntity>>(edit.Edge);
            if (_directed)
            {
                if (!Equal(before.Source, edit.Source)) Arrive(edit.Edge, edit.Source, false);
                if (!Equal(before.Target, edit.Target)) Arrive(edit.Edge, edit.Target, true);
            }
            else
            {
                if (!Equal(edit.Source, before.Source) && !Equal(edit.Source, before.Target)) Arrive(edit.Edge, edit.Source, false);
                if (!Equal(edit.Target, edit.Source) && !Equal(edit.Target, before.Source) && !Equal(edit.Target, before.Target)) Arrive(edit.Edge, edit.Target, false);
            }
        }
        heads.Clear();
    }

    private void ReserveFutureOrders()
    {
        foreach (var pair in _futureOrders)
        {
            if (!Active(pair.Key.Entity)) continue;
            ref Head head = ref GetHead(pair.Key.Entity, pair.Key.Incoming);
            if (pair.Value.Count <= (head.Items?.Length ?? 0)) continue;
            var items = ArrayPool<ReferenceRelationEntry<TEntity>>.Shared.Rent(pair.Value.Count);
            if (head.Items is { } previous)
            {
                previous.AsSpan(0, head.Count).CopyTo(items);
                ArrayPool<ReferenceRelationEntry<TEntity>>.Shared.Return(previous,
                    RuntimeHelpers.IsReferenceOrContainsReferences<ReferenceRelationEntry<TEntity>>());
            }
            head.Items = items;
        }
    }
}
