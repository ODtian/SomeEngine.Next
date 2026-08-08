using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;

namespace SomeEngine.ECS.Relations;

internal sealed partial class RelationGeneration<T>
    where T : struct, IComponent
{
    private static readonly RelationAdjacencyShard<T> s_empty =
        new UnorderedRelationAdjacencyShard<T>(Array.Empty<RelationAdjacencyEntry<T>>());
    private int _shared;
    private Dictionary<Entity, MutableRelationAdjacencyShard<T>>? _mutableOutgoing;
    private Dictionary<Entity, MutableRelationAdjacencyShard<T>>? _mutableIncoming;
    private Dictionary<Entity, MutableRelationAdjacencyShard<T>>? _mutableIncident;

    private RelationGeneration(
        RelationSchema schema,
        uint id,
        TopologyOrderDiagnosticCounter orderDiagnostics,
        RelationAdjacencyBatchDiagnosticCounter adjacencyBatchDiagnostics)
    {
        Schema = schema;
        Id = id;
        OrderDiagnostics = orderDiagnostics;
        AdjacencyBatchDiagnostics = adjacencyBatchDiagnostics;
        Edges = new RelationEntityMap<byte>();
        Outgoing = new RelationEntityMap<RelationAdjacencyShard<T>>();
        Incoming = new RelationEntityMap<RelationAdjacencyShard<T>>();
        Incident = new RelationEntityMap<RelationAdjacencyShard<T>>();
        OrderedShardCount = 0;
    }

    private RelationGeneration(
        RelationGeneration<T> source,
        uint id,
        TopologyOrderDiagnosticCounter orderDiagnostics,
        RelationAdjacencyBatchDiagnosticCounter adjacencyBatchDiagnostics)
    {
        if (source._cardinalityWorkspace is not null)
        {
            throw new InvalidOperationException(
                "A relation generation cannot be cloned during a cardinality transition or topology import.");
        }

        Schema = source.Schema;
        Id = id;
        OrderDiagnostics = orderDiagnostics;
        AdjacencyBatchDiagnostics = adjacencyBatchDiagnostics;
        Edges = source.Edges.CloneDetached();
        Outgoing = source.Outgoing.CloneDetached();
        Incoming = source.Incoming.CloneDetached();
        Incident = source.Incident.CloneDetached();
        CopyMutableImage(source._mutableOutgoing, Outgoing, adjacencyBatchDiagnostics);
        CopyMutableImage(source._mutableIncoming, Incoming, adjacencyBatchDiagnostics);
        CopyMutableImage(source._mutableIncident, Incident, adjacencyBatchDiagnostics);
        OrderedShardCount = source.OrderedShardCount;
    }

    private static void CopyMutableImage(
        Dictionary<Entity, MutableRelationAdjacencyShard<T>>? source,
        RelationEntityMap<RelationAdjacencyShard<T>> destination,
        RelationAdjacencyBatchDiagnosticCounter diagnostics)
    {
        if (source is null)
            return;

        foreach (var pair in source)
        {
            RelationAdjacencyShard<T>? snapshot = pair.Value.Freeze();
            diagnostics.RecordSourceCopy(pair.Value.Count);
            if (snapshot is null)
                destination.Remove(pair.Key);
            else
                destination[pair.Key] = snapshot;
        }
    }

    internal RelationSchema Schema { get; }

    private TopologyOrderDiagnosticCounter OrderDiagnostics { get; }

    private RelationAdjacencyBatchDiagnosticCounter AdjacencyBatchDiagnostics { get; }

    internal uint Id { get; }

    internal RelationEntityMap<byte> Edges { get; }

    internal RelationEntityMap<RelationAdjacencyShard<T>> Outgoing { get; }

    internal RelationEntityMap<RelationAdjacencyShard<T>> Incoming { get; }

    internal RelationEntityMap<RelationAdjacencyShard<T>> Incident { get; }

    internal int OrderedShardCount { get; private set; }

    internal void ImportEdge(RelationEdge<T> edge, RelationEndpointPair endpoints)
    {
        if (!Edges.TryAdd(edge.Entity, 0))
            throw new InvalidDataException($"Duplicate serialized relation edge {edge.Entity}.");
        try
        {
            ValidateNewEdge(endpoints, firstInsertIndex: null, secondInsertIndex: null);
            AttachCardinality(edge, endpoints);
        }
        catch
        {
            Edges.Remove(edge.Entity);
            throw;
        }
    }

    internal void ImportShard(
        Entity endpoint,
        RelationAdjacencyRole role,
        RelationAdjacencyEntry<T>[] ownedEntries,
        RelationAdjacencyOrderPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(ownedEntries);
        RelationEntityMap<RelationAdjacencyShard<T>> shards = DictionaryFor(role);
        if (shards.ContainsKey(endpoint))
        {
            throw new InvalidDataException(
                $"Duplicate serialized {role} adjacency for endpoint {endpoint}.");
        }

        RelationAdjacencyShard<T> shard = policy switch
        {
            RelationAdjacencyOrderPolicy.Ordered =>
                new OrderedRelationAdjacencyShard<T>(ownedEntries),
            RelationAdjacencyOrderPolicy.Unordered =>
                new UnorderedRelationAdjacencyShard<T>(ownedEntries),
            _ => throw new InvalidDataException($"Unknown relation adjacency policy {(byte)policy}."),
        };
        shards.Add(endpoint, shard);
        if (policy == RelationAdjacencyOrderPolicy.Ordered)
            OrderedShardCount = checked(OrderedShardCount + 1);
    }

    internal static RelationGeneration<T> Empty(
        RelationSchema schema,
        TopologyOrderDiagnosticCounter orderDiagnostics,
        RelationAdjacencyBatchDiagnosticCounter adjacencyBatchDiagnostics) =>
        new(schema, 1, orderDiagnostics, adjacencyBatchDiagnostics);

    internal bool IsShared => Volatile.Read(ref _shared) != 0;

    internal bool IsAdjacencyBatchActive =>
        _mutableOutgoing is not null || _mutableIncoming is not null || _mutableIncident is not null;

    internal void MarkShared() => Volatile.Write(ref _shared, 1);

    internal RelationGeneration<T> CloneNext(
        TopologyOrderDiagnosticCounter orderDiagnostics,
        RelationAdjacencyBatchDiagnosticCounter adjacencyBatchDiagnostics)
    {
        uint next = unchecked(Id + 1);
        return new(
            this,
            next == 0 ? 1 : next,
            orderDiagnostics,
            adjacencyBatchDiagnostics);
    }

    internal void BeginAdjacencyBatch()
    {
        if (_mutableOutgoing is not null || _mutableIncoming is not null || _mutableIncident is not null)
            throw new InvalidOperationException("A relation adjacency batch is already active.");

        if (Schema.Direction == RelationDirection.Directed)
        {
            _mutableOutgoing = new Dictionary<Entity, MutableRelationAdjacencyShard<T>>();
            _mutableIncoming = new Dictionary<Entity, MutableRelationAdjacencyShard<T>>();
        }
        else
        {
            _mutableIncident = new Dictionary<Entity, MutableRelationAdjacencyShard<T>>();
        }
    }

    internal void FreezeAdjacencyBatch()
    {
        FreezeMutableShards(Outgoing, _mutableOutgoing);
        FreezeMutableShards(Incoming, _mutableIncoming);
        FreezeMutableShards(Incident, _mutableIncident);
        _mutableOutgoing = null;
        _mutableIncoming = null;
        _mutableIncident = null;
    }

    private void FreezeMutableShards(
        RelationEntityMap<RelationAdjacencyShard<T>> shards,
        Dictionary<Entity, MutableRelationAdjacencyShard<T>>? mutableShards)
    {
        if (mutableShards is null)
            return;

        foreach (var pair in mutableShards)
        {
            RelationAdjacencyShard<T>? frozen = pair.Value.Freeze();
            AdjacencyBatchDiagnostics.RecordFreeze(pair.Value.Count);
            if (frozen is null)
                shards.Remove(pair.Key);
            else
                shards[pair.Key] = frozen;
        }
    }

    internal RelationAdjacencyShard<T> GetShard(Entity endpoint, RelationAdjacencyRole role)
    {
        Dictionary<Entity, MutableRelationAdjacencyShard<T>>? mutable = MutableDictionaryFor(role);
        if (mutable is not null && mutable.TryGetValue(endpoint, out var mutableShard))
            return mutableShard.Snapshot();
        RelationEntityMap<RelationAdjacencyShard<T>> shards = DictionaryFor(role);
        return shards.TryGetValue(endpoint, out RelationAdjacencyShard<T> shard)
            ? shard
            : s_empty;
    }

    internal (int Count, RelationAdjacencyOrderPolicy Policy) GetShardMetrics(
        Entity endpoint,
        RelationAdjacencyRole role)
    {
        Dictionary<Entity, MutableRelationAdjacencyShard<T>>? mutable = MutableDictionaryFor(role);
        if (mutable is not null && mutable.TryGetValue(endpoint, out var mutableShard))
            return (mutableShard.Count, mutableShard.Policy);
        RelationAdjacencyShard<T> shard = DictionaryFor(role).TryGetValue(
            endpoint,
            out RelationAdjacencyShard<T> existing)
            ? existing
            : s_empty;
        return (shard.Entries.Length, shard.Policy);
    }

    internal RelationAdjacencyOrderPolicy GetShardPolicy(
        Entity endpoint,
        RelationAdjacencyRole role) => GetShardMetrics(endpoint, role).Policy;

    internal long CountOrderedMembers()
    {
        RequireFrozenTopologyImage();
        long count = 0;
        if (Schema.Direction == RelationDirection.Directed)
        {
            count = checked(
                CountOrderedMembers(Outgoing) +
                CountOrderedMembers(Incoming));
        }
        else
        {
            count = CountOrderedMembers(Incident);
        }

        return count;
    }

    internal int CountOrderedMemberships(RelationEndpointPair endpoints)
    {
        if (Schema.Direction == RelationDirection.Directed)
        {
            int count = 0;
            if (GetShardPolicy(endpoints.First, RelationAdjacencyRole.Outgoing) ==
                RelationAdjacencyOrderPolicy.Ordered)
            {
                count++;
            }
            if (GetShardPolicy(endpoints.Second, RelationAdjacencyRole.Incoming) ==
                RelationAdjacencyOrderPolicy.Ordered)
            {
                count++;
            }
            return count;
        }

        int incident = GetShardPolicy(endpoints.First, RelationAdjacencyRole.Incident) ==
            RelationAdjacencyOrderPolicy.Ordered
                ? 1
                : 0;
        if (endpoints.Second != endpoints.First &&
            GetShardPolicy(endpoints.Second, RelationAdjacencyRole.Incident) ==
                RelationAdjacencyOrderPolicy.Ordered)
        {
            incident++;
        }
        return incident;
    }

    internal (Entity Endpoint, RelationAdjacencyRole Role)[] OrderedShardKeysStable()
    {
        RequireFrozenTopologyImage();
        var keys = new (Entity Endpoint, RelationAdjacencyRole Role)[OrderedShardCount];
        int offset = 0;
        if (Schema.Direction == RelationDirection.Directed)
        {
            AppendOrderedKeys(Outgoing, RelationAdjacencyRole.Outgoing, keys, ref offset);
            AppendOrderedKeys(Incoming, RelationAdjacencyRole.Incoming, keys, ref offset);
        }
        else
        {
            AppendOrderedKeys(Incident, RelationAdjacencyRole.Incident, keys, ref offset);
        }

        if (offset != keys.Length)
            throw new InvalidOperationException("Relation ordered-shard index is inconsistent.");
        Array.Sort(keys, static (left, right) =>
        {
            int endpoint = CompareTopologyEntities(left.Endpoint, right.Endpoint);
            return endpoint != 0 ? endpoint : left.Role.CompareTo(right.Role);
        });
        return keys;
    }

    private static long CountOrderedMembers(
        RelationEntityMap<RelationAdjacencyShard<T>> shards)
    {
        long count = 0;
        foreach (KeyValuePair<Entity, RelationAdjacencyShard<T>> pair in shards)
        {
            RelationAdjacencyShard<T> shard = pair.Value;
            if (shard.Policy == RelationAdjacencyOrderPolicy.Ordered)
                count = checked(count + shard.Entries.Length);
        }
        return count;
    }

    private static void AppendOrderedKeys(
        RelationEntityMap<RelationAdjacencyShard<T>> shards,
        RelationAdjacencyRole role,
        (Entity Endpoint, RelationAdjacencyRole Role)[] destination,
        ref int offset)
    {
        foreach (KeyValuePair<Entity, RelationAdjacencyShard<T>> pair in shards)
        {
            RelationAdjacencyShard<T> shard = pair.Value;
            if (shard.Policy != RelationAdjacencyOrderPolicy.Ordered)
                continue;
            if ((uint)offset >= (uint)destination.Length)
                throw new InvalidOperationException("Relation ordered-shard index is inconsistent.");
            destination[offset++] = (pair.Key, role);
        }
    }

    private void RequireFrozenTopologyImage()
    {
        if (IsAdjacencyBatchActive)
        {
            throw new InvalidOperationException(
                "Relation topology cannot be captured while an adjacency batch is active.");
        }
    }

    private static int CompareTopologyEntities(Entity left, Entity right)
    {
        int index = left.Index.CompareTo(right.Index);
        return index != 0 ? index : left.Generation.CompareTo(right.Generation);
    }

}
