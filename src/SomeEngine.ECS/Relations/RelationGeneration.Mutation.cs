using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;

namespace SomeEngine.ECS.Relations;

internal sealed partial class RelationGeneration<T>
    where T : struct, IComponent
{
    internal bool HasEndpointState(Entity endpoint)
    {
        if (Schema.Direction == RelationDirection.Directed)
        {
            return HasShardState(Outgoing, _mutableOutgoing, endpoint) ||
                   HasShardState(Incoming, _mutableIncoming, endpoint);
        }

        return HasShardState(Incident, _mutableIncident, endpoint);
    }

    private static bool HasShardState(
        RelationEntityMap<RelationAdjacencyShard<T>> immutable,
        Dictionary<Entity, MutableRelationAdjacencyShard<T>>? mutable,
        Entity endpoint)
    {
        if (mutable is not null && mutable.TryGetValue(endpoint, out var overlay))
        {
            return overlay.Count != 0 ||
                   overlay.Policy == RelationAdjacencyOrderPolicy.Ordered;
        }

        return immutable.ContainsKey(endpoint);
    }

    internal bool HasOrderedAdjacency(RelationEndpointPair endpoints)
    {
        if (OrderedShardCount == 0)
            return false;

        if (Schema.Direction == RelationDirection.Directed)
        {
            return GetShardPolicy(endpoints.First, RelationAdjacencyRole.Outgoing) ==
                       RelationAdjacencyOrderPolicy.Ordered ||
                   GetShardPolicy(endpoints.Second, RelationAdjacencyRole.Incoming) ==
                       RelationAdjacencyOrderPolicy.Ordered;
        }

        return GetShardPolicy(endpoints.First, RelationAdjacencyRole.Incident) ==
                   RelationAdjacencyOrderPolicy.Ordered ||
               GetShardPolicy(endpoints.Second, RelationAdjacencyRole.Incident) ==
                   RelationAdjacencyOrderPolicy.Ordered;
    }

    internal void Attach(
        RelationEdge<T> edge,
        RelationEndpointPair endpoints,
        int? firstInsertIndex,
        int? secondInsertIndex)
    {
        AddCardinality(edge, endpoints);
        AttachAdjacency(edge, endpoints, firstInsertIndex, secondInsertIndex);
    }

    internal void AttachCardinality(RelationEdge<T> edge, RelationEndpointPair endpoints) =>
        AddCardinality(edge, endpoints);

    internal void AttachAdjacency(
        RelationEdge<T> edge,
        RelationEndpointPair endpoints,
        int? firstInsertIndex,
        int? secondInsertIndex)
    {
        if (Schema.Direction == RelationDirection.Directed)
        {
            AddToShard(
                Outgoing,
                endpoints.First,
                new RelationAdjacencyEntry<T>(edge, endpoints.Second),
                firstInsertIndex);
            AddToShard(
                Incoming,
                endpoints.Second,
                new RelationAdjacencyEntry<T>(edge, endpoints.First),
                secondInsertIndex);
            CompleteCardinalityAttachment(edge);
            return;
        }

        if (endpoints.First == endpoints.Second)
        {
            if (firstInsertIndex is not null && secondInsertIndex is not null &&
                firstInsertIndex != secondInsertIndex)
            {
                throw new InvalidOperationException(
                    "An undirected self-edge has one incident membership and cannot receive two different positions.");
            }

            AddToShard(
                Incident,
                endpoints.First,
                new RelationAdjacencyEntry<T>(edge, endpoints.First),
                firstInsertIndex ?? secondInsertIndex);
            CompleteCardinalityAttachment(edge);
            return;
        }

        AddToShard(
            Incident,
            endpoints.First,
            new RelationAdjacencyEntry<T>(edge, endpoints.Second),
            firstInsertIndex);
        AddToShard(
            Incident,
            endpoints.Second,
            new RelationAdjacencyEntry<T>(edge, endpoints.First),
            secondInsertIndex);
        CompleteCardinalityAttachment(edge);
    }

    internal void Detach(RelationEdge<T> edge, RelationEndpointPair endpoints)
    {
        RemoveCardinality(edge, endpoints);
        DetachAdjacency(edge, endpoints);
    }

    internal void DetachCardinality(RelationEdge<T> edge, RelationEndpointPair endpoints) =>
        RemoveCardinality(edge, endpoints);

    internal void DetachAdjacency(RelationEdge<T> edge, RelationEndpointPair endpoints)
    {
        if (Schema.Direction == RelationDirection.Directed)
        {
            RemoveFromShard(Outgoing, endpoints.First, edge);
            RemoveFromShard(Incoming, endpoints.Second, edge);
            CompleteCardinalityDetachment(edge);
            return;
        }

        RemoveFromShard(Incident, endpoints.First, edge);
        if (endpoints.Second != endpoints.First)
            RemoveFromShard(Incident, endpoints.Second, edge);
        CompleteCardinalityDetachment(edge);
    }

    internal void ApplyPlacement(
        RelationEdge<T> edge,
        RelationEndpointPair endpoints,
        int? firstInsertIndex,
        int? secondInsertIndex)
    {
        if (firstInsertIndex is null && secondInsertIndex is null)
            return;

        if (Schema.Direction == RelationDirection.Directed)
        {
            if (firstInsertIndex is int outgoingIndex)
                Reorder(endpoints.First, RelationAdjacencyRole.Outgoing, edge, outgoingIndex);
            if (secondInsertIndex is int incomingIndex)
                Reorder(endpoints.Second, RelationAdjacencyRole.Incoming, edge, incomingIndex);
            return;
        }

        if (endpoints.First == endpoints.Second)
        {
            if (firstInsertIndex is not null && secondInsertIndex is not null &&
                firstInsertIndex != secondInsertIndex)
            {
                throw new InvalidOperationException(
                    "An undirected self-edge has one incident membership and cannot receive two different positions.");
            }

            if ((firstInsertIndex ?? secondInsertIndex) is int selfIndex)
                Reorder(endpoints.First, RelationAdjacencyRole.Incident, edge, selfIndex);
            return;
        }

        if (firstInsertIndex is int endpointAIndex)
            Reorder(endpoints.First, RelationAdjacencyRole.Incident, edge, endpointAIndex);
        if (secondInsertIndex is int endpointBIndex)
            Reorder(endpoints.Second, RelationAdjacencyRole.Incident, edge, endpointBIndex);
    }

    internal void SetOrderPolicy(
        Entity endpoint,
        RelationAdjacencyRole role,
        RelationAdjacencyOrderPolicy policy)
    {
        if (policy != RelationAdjacencyOrderPolicy.Unordered &&
            policy != RelationAdjacencyOrderPolicy.Ordered)
        {
            throw new ArgumentOutOfRangeException(nameof(policy), policy, "Unknown relation order policy.");
        }

        RelationEntityMap<RelationAdjacencyShard<T>> shards = DictionaryFor(role);
        Dictionary<Entity, MutableRelationAdjacencyShard<T>>? mutable = MutableDictionaryFor(role);
        if (mutable is not null)
        {
            RelationAdjacencyOrderPolicy currentPolicy = mutable.TryGetValue(endpoint, out var currentMutable)
                ? currentMutable.Policy
                : shards.TryGetValue(endpoint, out RelationAdjacencyShard<T> currentImmutable)
                    ? currentImmutable.Policy
                    : RelationAdjacencyOrderPolicy.Unordered;
            if (currentPolicy == policy)
                return;

            MutableRelationAdjacencyShard<T> builder =
                currentMutable ?? GetOrCreateMutableShard(shards, endpoint);
            builder.SetPolicy(policy);
            OrderedShardCount += policy == RelationAdjacencyOrderPolicy.Ordered ? 1 : -1;
            return;
        }

        RelationAdjacencyShard<T> current = shards.TryGetValue(
            endpoint,
            out RelationAdjacencyShard<T> existing)
            ? existing
            : s_empty;
        if (current.Policy == policy)
            return;

        var entries = current.Entries.ToArray();
        if (policy == RelationAdjacencyOrderPolicy.Ordered)
        {
            OrderDiagnostics.RecordOrderedPath();
            Array.Sort(entries, static (left, right) =>
            {
                int index = left.Edge.Entity.Index.CompareTo(right.Edge.Entity.Index);
                return index != 0
                    ? index
                    : left.Edge.Entity.Generation.CompareTo(right.Edge.Entity.Generation);
            });
            OrderDiagnostics.RecordOrderedIndexWork(entries.Length);
            shards[endpoint] = new OrderedRelationAdjacencyShard<T>(entries);
            OrderedShardCount++;
            return;
        }

        OrderedShardCount--;
        if (entries.Length == 0)
            shards.Remove(endpoint);
        else
            shards[endpoint] = new UnorderedRelationAdjacencyShard<T>(entries);
    }

    internal bool DropEmptyEndpoint(Entity endpoint)
    {
        bool removed = false;
        if (Schema.Direction == RelationDirection.Directed)
        {
            removed |= DropEmptyShard(Outgoing, endpoint, RelationAdjacencyRole.Outgoing);
            removed |= DropEmptyShard(Incoming, endpoint, RelationAdjacencyRole.Incoming);
        }
        else
        {
            removed |= DropEmptyShard(Incident, endpoint, RelationAdjacencyRole.Incident);
        }

        return removed;
    }

    private bool DropEmptyShard(
        RelationEntityMap<RelationAdjacencyShard<T>> shards,
        Entity endpoint,
        RelationAdjacencyRole role)
    {
        Dictionary<Entity, MutableRelationAdjacencyShard<T>>? mutable = MutableDictionaryFor(shards);
        if (mutable is not null && mutable.TryGetValue(endpoint, out var mutableShard))
        {
            if (mutableShard.Count != 0)
            {
                throw new InvalidOperationException(
                    $"Cannot retire {role} adjacency for {endpoint} while it still contains edges.");
            }

            if (mutableShard.Policy == RelationAdjacencyOrderPolicy.Ordered)
                OrderedShardCount--;
            mutable.Remove(endpoint);
            shards.Remove(endpoint);
            return true;
        }

        if (!shards.TryGetValue(endpoint, out RelationAdjacencyShard<T> shard))
            return false;
        if (shard.Entries.Length != 0)
        {
            throw new InvalidOperationException(
                $"Cannot retire {role} adjacency for {endpoint} while it still contains edges.");
        }

        if (shard.Policy == RelationAdjacencyOrderPolicy.Ordered)
            OrderedShardCount--;
        shards.Remove(endpoint);
        return true;
    }

    internal bool Reorder(
        Entity endpoint,
        RelationAdjacencyRole role,
        RelationEdge<T> edge,
        int insertIndex)
    {
        RelationEntityMap<RelationAdjacencyShard<T>> shards = DictionaryFor(role);
        Dictionary<Entity, MutableRelationAdjacencyShard<T>>? mutable = MutableDictionaryFor(role);
        if (mutable is not null)
        {
            MutableRelationAdjacencyShard<T> builder = GetOrCreateMutableShard(shards, endpoint);
            if (builder.Policy != RelationAdjacencyOrderPolicy.Ordered)
            {
                throw new InvalidOperationException(
                    $"{role} adjacency for endpoint {endpoint} is not ordered.");
            }
            if (!builder.Contains(edge))
            {
                throw new InvalidOperationException(
                    $"Edge {edge.Entity} is not in endpoint {endpoint}'s {role} adjacency.");
            }
            if ((uint)insertIndex >= (uint)builder.Count)
                throw new ArgumentOutOfRangeException(nameof(insertIndex));
            return builder.Reorder(edge, insertIndex);
        }

        if (!shards.TryGetValue(endpoint, out RelationAdjacencyShard<T> shard) ||
            shard.Policy != RelationAdjacencyOrderPolicy.Ordered)
        {
            throw new InvalidOperationException(
                $"{role} adjacency for endpoint {endpoint} is not ordered.");
        }
        OrderDiagnostics.RecordOrderedPath();

        int oldIndex = FindEdge(shard.Entries, edge);
        if (oldIndex < 0)
            throw new InvalidOperationException($"Edge {edge.Entity} is not in endpoint {endpoint}'s {role} adjacency.");
        if ((uint)insertIndex >= (uint)shard.Entries.Length)
            throw new ArgumentOutOfRangeException(nameof(insertIndex));
        if (oldIndex == insertIndex)
            return false;

        var entries = shard.Entries.ToArray();
        var value = entries[oldIndex];
        if (oldIndex < insertIndex)
            Array.Copy(entries, oldIndex + 1, entries, oldIndex, insertIndex - oldIndex);
        else
            Array.Copy(entries, insertIndex, entries, insertIndex + 1, oldIndex - insertIndex);
        entries[insertIndex] = value;
        OrderDiagnostics.RecordOrderedIndexWork(Math.Abs(insertIndex - oldIndex) + 1);
        shards[endpoint] = new OrderedRelationAdjacencyShard<T>(entries);
        return true;
    }

    private RelationEntityMap<RelationAdjacencyShard<T>> DictionaryFor(RelationAdjacencyRole role)
    {
        return role switch
        {
            RelationAdjacencyRole.Outgoing when Schema.Direction == RelationDirection.Directed => Outgoing,
            RelationAdjacencyRole.Incoming when Schema.Direction == RelationDirection.Directed => Incoming,
            RelationAdjacencyRole.Incident when Schema.Direction == RelationDirection.Undirected => Incident,
            _ => throw new InvalidOperationException(
                $"Adjacency role {role} is not valid for {Schema.Direction} relation {typeof(T).Name}."),
        };
    }

    private Dictionary<Entity, MutableRelationAdjacencyShard<T>>? MutableDictionaryFor(
        RelationAdjacencyRole role)
    {
        _ = DictionaryFor(role);
        return role switch
        {
            RelationAdjacencyRole.Outgoing => _mutableOutgoing,
            RelationAdjacencyRole.Incoming => _mutableIncoming,
            RelationAdjacencyRole.Incident => _mutableIncident,
            _ => null,
        };
    }

    private Dictionary<Entity, MutableRelationAdjacencyShard<T>>? MutableDictionaryFor(
        RelationEntityMap<RelationAdjacencyShard<T>> shards)
    {
        if (ReferenceEquals(shards, Outgoing))
            return _mutableOutgoing;
        if (ReferenceEquals(shards, Incoming))
            return _mutableIncoming;
        if (ReferenceEquals(shards, Incident))
            return _mutableIncident;
        throw new InvalidOperationException("Relation adjacency dictionary does not belong to this generation.");
    }

    private MutableRelationAdjacencyShard<T> GetOrCreateMutableShard(
        RelationEntityMap<RelationAdjacencyShard<T>> shards,
        Entity endpoint)
    {
        Dictionary<Entity, MutableRelationAdjacencyShard<T>> mutable =
            MutableDictionaryFor(shards) ??
            throw new InvalidOperationException("No relation adjacency batch is active.");
        if (mutable.TryGetValue(endpoint, out var existing))
            return existing;

        RelationAdjacencyShard<T> source = shards.TryGetValue(
            endpoint,
            out RelationAdjacencyShard<T> immutable)
            ? immutable
            : s_empty;
        AdjacencyBatchDiagnostics.RecordSourceCopy(source.Entries.Length);
        var created = new MutableRelationAdjacencyShard<T>(source, OrderDiagnostics);
        mutable.Add(endpoint, created);
        return created;
    }

    internal void ValidateNewEdge(
        RelationEndpointPair endpoints,
        int? firstInsertIndex,
        int? secondInsertIndex)
    {
        ValidateCardinality(endpoints, Entity.Null);

        if (Schema.Direction == RelationDirection.Directed)
        {
            ValidateInsertIndex(
                GetShardMetrics(endpoints.First, RelationAdjacencyRole.Outgoing),
                endpoints.First,
                firstInsertIndex);
            ValidateInsertIndex(
                GetShardMetrics(endpoints.Second, RelationAdjacencyRole.Incoming),
                endpoints.Second,
                secondInsertIndex);
            return;
        }

        if (endpoints.First == endpoints.Second)
        {
            if (firstInsertIndex is not null && secondInsertIndex is not null &&
                firstInsertIndex != secondInsertIndex)
            {
                throw new InvalidOperationException(
                    "An undirected self-edge has one incident membership and cannot receive two different positions.");
            }
            ValidateInsertIndex(
                GetShardMetrics(endpoints.First, RelationAdjacencyRole.Incident),
                endpoints.First,
                firstInsertIndex ?? secondInsertIndex);
            return;
        }

        ValidateInsertIndex(
            GetShardMetrics(endpoints.First, RelationAdjacencyRole.Incident),
            endpoints.First,
            firstInsertIndex);
        ValidateInsertIndex(
            GetShardMetrics(endpoints.Second, RelationAdjacencyRole.Incident),
            endpoints.Second,
            secondInsertIndex);
    }

    private static void ValidateInsertIndex(
        (int Count, RelationAdjacencyOrderPolicy Policy) shard,
        Entity endpoint,
        int? insertIndex)
    {
        if (insertIndex is null)
            return;
        if (shard.Policy == RelationAdjacencyOrderPolicy.Unordered)
            throw new InvalidOperationException($"Endpoint {endpoint}'s relation adjacency is unordered.");
        if ((uint)insertIndex.Value > (uint)shard.Count)
            throw new ArgumentOutOfRangeException(nameof(insertIndex));
    }

    private void AddToShard(
        RelationEntityMap<RelationAdjacencyShard<T>> shards,
        Entity endpoint,
        RelationAdjacencyEntry<T> entry,
        int? insertIndex)
    {
        if (MutableDictionaryFor(shards) is not null)
        {
            MutableRelationAdjacencyShard<T> builder = GetOrCreateMutableShard(shards, endpoint);
            if (builder.Contains(entry.Edge))
            {
                throw new InvalidOperationException(
                    $"Edge {entry.Edge.Entity} already exists in endpoint {endpoint}'s adjacency.");
            }
            if (builder.Policy == RelationAdjacencyOrderPolicy.Unordered && insertIndex is not null)
                throw new InvalidOperationException($"Endpoint {endpoint}'s relation adjacency is unordered.");
            builder.Add(entry, insertIndex);
            return;
        }

        RelationAdjacencyShard<T> shard = shards.TryGetValue(
            endpoint,
            out RelationAdjacencyShard<T> existing)
            ? existing
            : s_empty;
        if (FindEdge(shard.Entries, entry.Edge) >= 0)
            throw new InvalidOperationException($"Edge {entry.Edge.Entity} already exists in endpoint {endpoint}'s adjacency.");

        if (shard.Policy == RelationAdjacencyOrderPolicy.Unordered)
        {
            if (insertIndex is not null)
                throw new InvalidOperationException($"Endpoint {endpoint}'s relation adjacency is unordered.");

            var entries = new RelationAdjacencyEntry<T>[shard.Entries.Length + 1];
            shard.Entries.CopyTo(entries);
            entries[^1] = entry;
            shards[endpoint] = new UnorderedRelationAdjacencyShard<T>(entries);
            return;
        }

        OrderDiagnostics.RecordOrderedPath();
        int index = insertIndex ?? shard.Entries.Length;
        if ((uint)index > (uint)shard.Entries.Length)
            throw new ArgumentOutOfRangeException(nameof(insertIndex));
        var orderedEntries = new RelationAdjacencyEntry<T>[shard.Entries.Length + 1];
        shard.Entries[..index].CopyTo(orderedEntries);
        orderedEntries[index] = entry;
        shard.Entries[index..].CopyTo(orderedEntries.AsSpan(index + 1));
        OrderDiagnostics.RecordOrderedIndexWork(orderedEntries.Length - index);
        shards[endpoint] = new OrderedRelationAdjacencyShard<T>(orderedEntries);
    }

    private void RemoveFromShard(
        RelationEntityMap<RelationAdjacencyShard<T>> shards,
        Entity endpoint,
        RelationEdge<T> edge)
    {
        if (MutableDictionaryFor(shards) is not null)
        {
            MutableRelationAdjacencyShard<T> builder = GetOrCreateMutableShard(shards, endpoint);
            if (!builder.Contains(edge))
            {
                throw new InvalidOperationException(
                    $"Endpoint {endpoint} does not contain edge {edge.Entity}.");
            }
            builder.Remove(edge);
            return;
        }

        if (!shards.TryGetValue(endpoint, out RelationAdjacencyShard<T> shard))
            throw new InvalidOperationException($"Endpoint {endpoint} has no adjacency for edge {edge.Entity}.");
        int index = FindEdge(shard.Entries, edge);
        if (index < 0)
            throw new InvalidOperationException($"Endpoint {endpoint} does not contain edge {edge.Entity}.");

        if (shard.Entries.Length == 1)
        {
            if (shard.Policy == RelationAdjacencyOrderPolicy.Ordered)
            {
                OrderDiagnostics.RecordOrderedPath();
                OrderDiagnostics.RecordOrderedIndexWork(1);
                shards[endpoint] = new OrderedRelationAdjacencyShard<T>(
                    Array.Empty<RelationAdjacencyEntry<T>>());
            }
            else
            {
                shards.Remove(endpoint);
            }
            return;
        }

        var entries = new RelationAdjacencyEntry<T>[shard.Entries.Length - 1];
        if (shard.Policy == RelationAdjacencyOrderPolicy.Ordered)
        {
            OrderDiagnostics.RecordOrderedPath();
            shard.Entries[..index].CopyTo(entries);
            shard.Entries[(index + 1)..].CopyTo(entries.AsSpan(index));
            OrderDiagnostics.RecordOrderedIndexWork(shard.Entries.Length - index);
        }
        else
        {
            shard.Entries[..entries.Length].CopyTo(entries);
            if (index != shard.Entries.Length - 1)
                entries[index] = shard.Entries[^1];
        }

        RelationAdjacencyShard<T> replacement =
            shard.Policy == RelationAdjacencyOrderPolicy.Ordered
                ? new OrderedRelationAdjacencyShard<T>(entries)
                : new UnorderedRelationAdjacencyShard<T>(entries);
        shards[endpoint] = replacement;
    }

    private static int FindEdge(
        ReadOnlySpan<RelationAdjacencyEntry<T>> entries,
        RelationEdge<T> edge)
    {
        for (int i = 0; i < entries.Length; i++)
        {
            if (entries[i].Edge == edge)
                return i;
        }

        return -1;
    }
}
