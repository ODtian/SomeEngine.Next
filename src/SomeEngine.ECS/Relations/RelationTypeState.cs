using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Owners;

namespace SomeEngine.ECS.Relations;

internal readonly record struct RelationSerializationValidationDiagnostics(
    long CompletedValidationCount,
    long EdgeVisits,
    long ShardVisits,
    long MembershipVisits);

internal sealed partial class RelationTypeState<T> : IRelationTypeState
    where T : struct, IComponent
{
    private readonly RelationEntityMap<byte> _dirtyEdges;
    private readonly RelationEntityMap<RelationPendingPlacement> _pendingPlacements;
    private readonly RelationEntityMap<RelationEndpointPair> _dirtyCurrentEndpoints;
    private readonly RelationEntityMap<RelationDirtyEdgeBucket> _dirtyEdgesByEndpoint;
    private readonly TopologyOrderDiagnosticCounter _orderDiagnostics;
    private readonly RelationAdjacencyBatchDiagnosticCounter _adjacencyBatchDiagnostics;
    private RelationGeneration<T> _generation;
    private RelationGeneration<T>? _commandBatchBase;
    private RelationGeneration<T>? _commandBatchGeneration;
    private Dictionary<Entity, RelationEndpointPair>? _commandBatchCanonicalEndpoints;
    private Dictionary<RelationEndpointPair, HashSet<Entity>>? _commandBatchCanonicalPairs;
    private Dictionary<RelationCanonicalEndpointKey, HashSet<Entity>>? _commandBatchCanonicalRoles;
    private bool _commandBatchHasChanges;
    private bool _commandBatchValidationHasSynchronizedDirtyRevision;
    private long _fullCloneCount;
    private long _commandBatchValidationFullScanCount;
    private long _commandBatchValidationTransitionVisitCount;
    private long _dirtySequence;
    private ulong _dirtyMutationRevision;
    private ulong _commandBatchValidationSyncedDirtyRevision;
    private long _bulkIndexBuildCount;
    private long _bulkIndexBuildEdgeVisits;
    private long _betweenLookupCount;
    private long _betweenBucketVisits;
    private long _atLookupCount;
    private long _atBucketVisits;
    private long _cleanupLookupCount;
    private long _cleanupAppliedEntryVisits;
    private long _cleanupDirtyEntryVisits;
    private long _serializationValidationCount;
    private long _serializationValidationEdgeVisits;
    private long _serializationValidationShardVisits;
    private long _serializationValidationMembershipVisits;

    internal RelationTypeState(RelationSchema schema)
    {
        Schema = schema;
        _dirtyEdges = new RelationEntityMap<byte>();
        _pendingPlacements = new RelationEntityMap<RelationPendingPlacement>();
        _dirtyCurrentEndpoints = new RelationEntityMap<RelationEndpointPair>();
        _dirtyEdgesByEndpoint = new RelationEntityMap<RelationDirtyEdgeBucket>();
        _orderDiagnostics = new TopologyOrderDiagnosticCounter();
        _adjacencyBatchDiagnostics = new RelationAdjacencyBatchDiagnosticCounter();
        _generation = RelationGeneration<T>.Empty(
            schema,
            _orderDiagnostics,
            _adjacencyBatchDiagnostics);
    }

    private RelationTypeState(RelationTypeState<T> source)
    {
        Schema = source.Schema;
        _dirtyEdges = source._dirtyEdges.CloneDetached();
        _pendingPlacements = source._pendingPlacements.CloneDetached();
        _dirtyCurrentEndpoints = source._dirtyCurrentEndpoints.CloneDetached();
        _dirtyEdgesByEndpoint = source._dirtyEdgesByEndpoint.CloneDetached();
        _orderDiagnostics = source._orderDiagnostics.CloneDetached();
        _adjacencyBatchDiagnostics = source._adjacencyBatchDiagnostics.CloneDetached();
        // A published relation generation is immutable. A structural candidate therefore keeps
        // the exact generation identity and pays for the mutable dictionaries/indexes only when
        // its first prepared transition is actually published. The dirty-placement workspace is
        // deliberately copied here because it remains mutable independently of a generation.
        _generation = Volatile.Read(ref source._generation);
        _generation.MarkShared();
        _dirtySequence = source._dirtySequence;
        _dirtyMutationRevision = source._dirtyMutationRevision;
        _fullCloneCount = source._fullCloneCount;
        _commandBatchValidationFullScanCount = source._commandBatchValidationFullScanCount;
        _commandBatchValidationTransitionVisitCount = source._commandBatchValidationTransitionVisitCount;
        _bulkIndexBuildCount = source._bulkIndexBuildCount;
        _bulkIndexBuildEdgeVisits = source._bulkIndexBuildEdgeVisits;
        _betweenLookupCount = source._betweenLookupCount;
        _betweenBucketVisits = source._betweenBucketVisits;
        _atLookupCount = source._atLookupCount;
        _atBucketVisits = source._atBucketVisits;
        _cleanupLookupCount = source._cleanupLookupCount;
        _cleanupAppliedEntryVisits = source._cleanupAppliedEntryVisits;
        _cleanupDirtyEntryVisits = source._cleanupDirtyEntryVisits;
        _serializationValidationCount = source._serializationValidationCount;
        _serializationValidationEdgeVisits = source._serializationValidationEdgeVisits;
        _serializationValidationShardVisits = source._serializationValidationShardVisits;
        _serializationValidationMembershipVisits = source._serializationValidationMembershipVisits;
    }

    public Type PayloadType => typeof(T);

    IRelationTypeState IRelationTypeState.CloneDetached() => CloneDetached();

    void IRelationTypeState.DestroyEdge(
        RelationGraph graph,
        World world,
        Entity edge,
        bool destroyEntity) =>
        graph.DestroyEdgeTyped(world, this, edge, destroyEntity);

    void IRelationTypeState.DestroyIncidentEdges(
        RelationGraph graph,
        World world,
        Entity endpoint,
        ExceptionAccumulator faults) =>
        DestroyIncidentEdges(graph, world, endpoint, faults);

    internal RelationTypeState<T> CloneDetached() => new(this);

    internal RelationSchema Schema { get; }

    internal uint Generation => CurrentGeneration.Id;

    internal int EdgeCount => CurrentGeneration.Edges.Count;

    /// <summary>The immutable-or-exclusively-owned relation generation used by this wrapper.</summary>
    internal object BackingIdentity => CurrentGeneration;

    /// <summary>Number of shared generations detached by a published write in this wrapper.</summary>
    internal int DetachCount { get; private set; }

    /// <summary>
    /// Number of complete generation copies made by this typed state. Command-batch regression
    /// tests use the monotonic counter to ensure a batch performs one copy, not one per edge.
    /// </summary>
    internal long FullCloneCount => _fullCloneCount;

    internal RelationAdjacencyBatchDiagnostics AdjacencyBatchDiagnostics =>
        _adjacencyBatchDiagnostics.Snapshot();

    internal RelationCommandBatchValidationDiagnostics CommandBatchValidationDiagnostics =>
        new(
            _commandBatchValidationFullScanCount,
            _commandBatchValidationTransitionVisitCount);

    internal RelationCanonicalLookupDiagnostics CanonicalLookupDiagnostics =>
        new(
            _bulkIndexBuildCount,
            _bulkIndexBuildEdgeVisits,
            _betweenLookupCount,
            _betweenBucketVisits,
            _atLookupCount,
            _atBucketVisits,
            _cleanupLookupCount,
            _cleanupAppliedEntryVisits,
            _cleanupDirtyEntryVisits);

    internal RelationSerializationValidationDiagnostics SerializationValidationDiagnostics =>
        new(
            _serializationValidationCount,
            _serializationValidationEdgeVisits,
            _serializationValidationShardVisits,
            _serializationValidationMembershipVisits);

    internal bool HasDirtyEdges => _dirtyEdges.Count != 0;

    internal bool HasSerializationWorkspace =>
        _commandBatchBase is not null ||
        _commandBatchGeneration is not null ||
        CurrentGeneration.IsAdjacencyBatchActive;

    internal bool IsDirty(Entity edge) => _dirtyEdges.ContainsKey(edge);

    internal RelationGeneration<T> SerializationGeneration => CurrentGeneration;

    internal long PrepareSerializationWrite(WorldStructureRoot root)
    {
        ArgumentNullException.ThrowIfNull(root);
        RelationGeneration<T> generation = CurrentGeneration;
        if (HasDirtyEdges || HasSerializationWorkspace)
        {
            throw new InvalidOperationException(
                $"Relation topology {typeof(T).FullName} has dirty or in-progress state and cannot be serialized without materializing a second generation.");
        }

        long edgeVisits = 0;
        long shardVisits = 0;
        long membershipVisits = 0;
        foreach (KeyValuePair<Entity, byte> pair in generation.Edges)
        {
            Entity edge = pair.Key;
            edgeVisits++;
            if (!root.Entities.Alive(edge) ||
                !root.Components.Has<T>(edge) ||
                !RelationEndpointAccess.HasCurrent<T>(root.Components, edge, Schema))
            {
                throw new InvalidOperationException(
                    $"Relation edge {edge} has no complete live canonical image during serialization.");
            }

            RelationEndpointPair current = RelationEndpointAccess.ReadCurrent<T>(
                root.Components,
                edge,
                Schema);
            RelationAppliedEndpointImage applied =
                RelationEndpointAccess.ReadAppliedImage<T>(root.Components, edge);
            if (!applied.IsApplied || applied.Endpoints != current)
            {
                throw new InvalidOperationException(
                    $"Relation edge {edge} has unapplied endpoint state and cannot be serialized without a preview generation.");
            }
        }

        // Call-local validation metadata only. It is never published into the relation generation
        // or retained by a shard, and is released when this preflight call returns.
        var membershipRoles = new Dictionary<Entity, byte>(generation.Edges.Count);
        int orderedShards = 0;
        if (Schema.Direction == RelationDirection.Directed)
        {
            ValidateSerializationShards(
                root,
                generation,
                generation.Outgoing,
                RelationAdjacencyRole.Outgoing,
                membershipRoles,
                ref orderedShards,
                ref shardVisits,
                ref membershipVisits);
            ValidateSerializationShards(
                root,
                generation,
                generation.Incoming,
                RelationAdjacencyRole.Incoming,
                membershipRoles,
                ref orderedShards,
                ref shardVisits,
                ref membershipVisits);
        }
        else
        {
            ValidateSerializationShards(
                root,
                generation,
                generation.Incident,
                RelationAdjacencyRole.Incident,
                membershipRoles,
                ref orderedShards,
                ref shardVisits,
                ref membershipVisits);
        }
        if (orderedShards != generation.OrderedShardCount)
            throw new InvalidOperationException("Relation ordered-shard count is inconsistent.");

        foreach (KeyValuePair<Entity, byte> pair in generation.Edges)
        {
            Entity edge = pair.Key;
            edgeVisits++;
            membershipRoles.TryGetValue(edge, out byte actualRoles);
            if (Schema.Direction == RelationDirection.Directed)
            {
                RequireSerializationMembership(
                    actualRoles,
                    FirstMembershipRole,
                    edge,
                    "outgoing");
                RequireSerializationMembership(
                    actualRoles,
                    SecondMembershipRole,
                    edge,
                    "incoming");
            }
            else
            {
                RelationEndpointPair endpoints = RelationEndpointAccess.ReadCurrent<T>(
                    root.Components,
                    edge,
                    Schema);
                RequireSerializationMembership(
                    actualRoles,
                    FirstMembershipRole,
                    edge,
                    "incident");
                if (endpoints.Second != endpoints.First)
                {
                    RequireSerializationMembership(
                        actualRoles,
                        SecondMembershipRole,
                        edge,
                        "incident");
                }
            }
        }

        _serializationValidationCount++;
        _serializationValidationEdgeVisits += edgeVisits;
        _serializationValidationShardVisits += shardVisits;
        _serializationValidationMembershipVisits += membershipVisits;
        return checked(
            generation.Edges.Count +
            (long)generation.OrderedShardCount +
            generation.CountOrderedMembers());
    }

    internal long GetValidatedSerializationRecordCount()
    {
        RelationGeneration<T> generation = CurrentGeneration;
        return checked(
            generation.Edges.Count +
            (long)generation.OrderedShardCount +
            generation.CountOrderedMembers());
    }

    private void ValidateSerializationShards(
        WorldStructureRoot root,
        RelationGeneration<T> generation,
        RelationEntityMap<RelationAdjacencyShard<T>> shards,
        RelationAdjacencyRole role,
        Dictionary<Entity, byte> membershipRoles,
        ref int orderedShards,
        ref long shardVisits,
        ref long membershipVisits)
    {
        foreach (KeyValuePair<Entity, RelationAdjacencyShard<T>> pair in shards)
        {
            shardVisits++;
            Entity endpoint = pair.Key;
            RelationAdjacencyShard<T> shard = pair.Value;
            if (!root.Entities.Alive(endpoint) || root.Entities.Pending(endpoint))
            {
                throw new InvalidOperationException(
                    $"Relation {role} shard endpoint {endpoint} is not live.");
            }
            if (shard.Policy == RelationAdjacencyOrderPolicy.Ordered)
                orderedShards = checked(orderedShards + 1);

            for (int i = 0; i < shard.Entries.Length; i++)
            {
                membershipVisits++;
                RelationAdjacencyEntry<T> entry = shard.Entries[i];
                Entity edge = entry.Edge.Entity;
                if (!generation.Edges.ContainsKey(edge))
                    throw new InvalidOperationException($"Relation {role} shard contains unknown edge {edge}.");

                RelationEndpointPair endpoints = RelationEndpointAccess.ReadCurrent<T>(
                    root.Components,
                    edge,
                    Schema);
                byte membershipRole = role switch
                {
                    RelationAdjacencyRole.Outgoing
                        when endpoints.First == endpoint && endpoints.Second == entry.OtherEndpoint =>
                        FirstMembershipRole,
                    RelationAdjacencyRole.Incoming
                        when endpoints.Second == endpoint && endpoints.First == entry.OtherEndpoint =>
                        SecondMembershipRole,
                    RelationAdjacencyRole.Incident
                        when endpoints.First == endpoint && endpoints.Second == entry.OtherEndpoint =>
                        FirstMembershipRole,
                    RelationAdjacencyRole.Incident
                        when endpoints.Second == endpoint && endpoints.First == entry.OtherEndpoint =>
                        SecondMembershipRole,
                    _ => 0,
                };
                if (membershipRole == 0)
                {
                    throw new InvalidOperationException(
                        $"Relation {role} shard entry for edge {edge} disagrees with canonical endpoints.");
                }

                membershipRoles.TryGetValue(edge, out byte currentRoles);
                if ((currentRoles & membershipRole) != 0)
                {
                    throw new InvalidOperationException(
                        $"Relation {role} shard for {endpoint} repeats edge {edge}.");
                }
                membershipRoles[edge] = (byte)(currentRoles | membershipRole);
            }
        }
    }

    private const byte FirstMembershipRole = 1 << 0;
    private const byte SecondMembershipRole = 1 << 1;

    private static void RequireSerializationMembership(
        byte actualRoles,
        byte expectedRole,
        Entity edge,
        string role)
    {
        int matches = (actualRoles & expectedRole) == 0 ? 0 : 1;
        if (matches != 1)
        {
            throw new InvalidOperationException(
                $"Relation edge {edge} has {matches} {role} adjacency memberships; expected exactly one.");
        }
    }

    internal bool TryGetSerializationOrderedShard(
        Entity endpoint,
        RelationAdjacencyRole role,
        out ReadOnlySpan<RelationAdjacencyEntry<T>> entries)
    {
        RelationGeneration<T> generation = CurrentGeneration;
        RelationAdjacencyShard<T> shard = generation.GetShard(endpoint, role);
        if (shard.Policy != RelationAdjacencyOrderPolicy.Ordered)
        {
            entries = ReadOnlySpan<RelationAdjacencyEntry<T>>.Empty;
            return false;
        }

        entries = shard.Entries;
        return true;
    }

    internal RelationGeneration<T> BeginTopologyImport()
    {
        RelationGeneration<T> generation = CurrentGeneration;
        if (HasDirtyEdges ||
            HasSerializationWorkspace ||
            generation.IsShared ||
            generation.Edges.Count != 0 ||
            generation.Outgoing.Count != 0 ||
            generation.Incoming.Count != 0 ||
            generation.Incident.Count != 0)
        {
            throw new InvalidDataException(
                $"Relation topology {typeof(T).FullName} import requires a new, empty World backing.");
        }

        return generation;
    }

    internal TopologyOrderDiagnostics OrderDiagnostics =>
        _orderDiagnostics.Snapshot(
            _pendingPlacements.Count,
            System.Runtime.CompilerServices.Unsafe.SizeOf<RelationPendingPlacement>());

    public bool IsEdge(Entity entity) => CurrentGeneration.Edges.ContainsKey(entity);

    public bool HasEndpointState(Entity endpoint)
    {
        RelationGeneration<T> generation = CurrentGeneration;
        return generation.HasEndpointState(endpoint);
    }

    public void DropEndpointState(Entity endpoint)
    {
        RelationGeneration<T> next = WritableGeneration(out RelationGeneration<T> generation);
        if (!next.DropEmptyEndpoint(endpoint))
            return;

        Publish(new PreparedRelationState<T>(
            generation,
            next,
            Array.Empty<RelationAffectedShard>()));
    }

    public void BeginCommandBatch()
    {
        if (_commandBatchBase is not null)
            throw new InvalidOperationException($"A {typeof(T).Name} relation command batch is already active.");

        _commandBatchBase = Volatile.Read(ref _generation);
        _commandBatchGeneration = null;
        _commandBatchCanonicalEndpoints = null;
        _commandBatchCanonicalPairs = null;
        _commandBatchCanonicalRoles = null;
        _commandBatchHasChanges = false;
        _commandBatchValidationHasSynchronizedDirtyRevision = false;
    }

    public void EndCommandBatch(bool completed)
    {
        RelationGeneration<T> commandBatchBase = _commandBatchBase ??
            throw new InvalidOperationException($"No {typeof(T).Name} relation command batch is active.");
        RelationGeneration<T>? commandBatchGeneration = _commandBatchGeneration;
        bool publish = completed && _commandBatchHasChanges && commandBatchGeneration is not null;

        if (publish)
            commandBatchGeneration!.FreezeAdjacencyBatch();

        _commandBatchBase = null;
        _commandBatchGeneration = null;
        _commandBatchCanonicalEndpoints = null;
        _commandBatchCanonicalPairs = null;
        _commandBatchCanonicalRoles = null;
        _commandBatchHasChanges = false;
        _commandBatchValidationHasSynchronizedDirtyRevision = false;

        if (publish)
            PublishGeneration(commandBatchBase, commandBatchGeneration!);
    }



}
