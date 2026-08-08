using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Owners;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Registry;
using SomeEngine.ECS.Serialization;

namespace SomeEngine.ECS.Serialization
{

internal sealed class HierarchyTopologyWriteAccess<TDomain>
    where TDomain : IHierarchyDomain
{
    private readonly HierarchyDomainStore<TDomain> _store;

    internal HierarchyTopologyWriteAccess(HierarchyDomainStore<TDomain> store)
    {
        _store = store;
        store.PrepareSerializationWrite(
            out int parentCount,
            out int orderedSequenceCount,
            out long recordCount);
        ParentCount = parentCount;
        OrderedSequenceCount = orderedSequenceCount;
        RecordCount = recordCount;
    }

    internal int SlotCount => _store.SerializationSlotCount;

    internal int ParentCount { get; }

    internal int OrderedSequenceCount { get; }

    internal long RecordCount { get; }

    internal bool TryGetParentAt(int slotOffset, out Entity child, out Entity parent) =>
        _store.TryGetSerializationParentAt(slotOffset, out child, out parent);

    internal bool TryGetOrderedChildrenAt(
        int slotOffset,
        out Entity parent,
        out ReadOnlyMemory<Entity> children) =>
        _store.TryGetSerializationOrderedChildrenAt(slotOffset, out parent, out children);
}

internal sealed class RelationTopologyWriteAccess<T>
    where T : struct, IComponent
{
    private readonly WorldStructureRoot _root;
    private readonly RelationTypeState<T>? _state;

    internal RelationTopologyWriteAccess(
        WorldStructureRoot root,
        bool validate)
    {
        _root = root;
        _state = root.RelationGraph.PrepareSerializationWrite<T>();
        Schema = RelationSchema.For<T>();
        SlotCount = root.Entities.Store.Count;
        if (_state is null)
            return;

        EdgeCount = _state.EdgeCount;
        OrderedSequenceCount = _state.SerializationGeneration.OrderedShardCount;
        RecordCount = validate
            ? _state.PrepareSerializationWrite(root)
            : _state.GetValidatedSerializationRecordCount();
    }

    internal RelationSchema Schema { get; }

    internal int SlotCount { get; }

    internal int EdgeCount { get; }

    internal int OrderedSequenceCount { get; }

    internal long RecordCount { get; }

    internal bool TryGetEdgeAt(
        int slotOffset,
        out Entity edge,
        out Entity first,
        out Entity second)
    {
        if (!TryGetLiveEntityAt(slotOffset, out edge) ||
            _state is null ||
            !_state.IsEdge(edge))
        {
            first = Entity.Null;
            second = Entity.Null;
            return false;
        }

        RelationEndpointPair endpoints = RelationEndpointAccess.ReadCurrent<T>(
            _root.Components,
            edge,
            Schema);
        first = endpoints.First;
        second = endpoints.Second;
        return true;
    }

    internal bool TryGetOrderedAt(
        int slotOffset,
        RelationAdjacencyRole role,
        out Entity endpoint,
        out ReadOnlySpan<RelationAdjacencyEntry<T>> entries)
    {
        if (!TryGetLiveEntityAt(slotOffset, out endpoint) || _state is null)
        {
            entries = ReadOnlySpan<RelationAdjacencyEntry<T>>.Empty;
            return false;
        }

        return _state.TryGetSerializationOrderedShard(endpoint, role, out entries);
    }

    private bool TryGetLiveEntityAt(int slotOffset, out Entity entity)
    {
        var store = _root.Entities.Store;
        int index = checked(slotOffset + 1);
        if ((uint)slotOffset >= (uint)store.Count || !store.IsAliveIndex(index))
        {
            entity = Entity.Null;
            return false;
        }

        entity = new Entity(index, store.GetGeneration(index));
        return true;
    }
}

internal sealed class RelationTopologyImport<T>
    where T : struct, IComponent
{
    internal sealed class MembershipPlan
    {
        internal int Count;
        internal int Offset;
        internal bool Ordered;
    }

    private readonly World _world;
    private readonly RelationGraph _graph;
    private readonly RelationTypeState<T> _state;
    private readonly RelationGeneration<T> _generation;
    private readonly int _expectedEdgeCount;
    private Dictionary<RelationCanonicalEndpointKey, MembershipPlan>? _memberships = new();
    private int _expectedSequenceCount = -1;
    private int _sequenceCount;
    private bool _completed;

    internal int RetainedMembershipMetadataCount => _memberships?.Count ?? 0;

    internal RelationTopologyImport(
        World world,
        RelationGraph graph,
        RelationDirection direction,
        RelationCardinality cardinality,
        bool allowSelfEdge,
        int edgeCount)
    {
        _world = world;
        _graph = graph;
        RelationSchema schema = RelationSchema.For<T>();
        if (direction != schema.Direction ||
            cardinality != schema.Cardinality ||
            allowSelfEdge != schema.AllowSelfEdge)
        {
            throw new InvalidDataException(
                $"Serialized relation schema for {typeof(T).FullName} does not match its registered runtime schema.");
        }

        _state = graph.BeginTopologyImport<T>(world);
        _generation = _state.BeginTopologyImport();
        _expectedEdgeCount = edgeCount;
    }

    internal void AddEdge(Entity edge, Entity first, Entity second)
    {
        RequireOpen();
        if (_generation.Edges.Count >= _expectedEdgeCount)
            throw new InvalidDataException("Relation payload contains more edges than declared.");

        var endpoints = new RelationEndpointPair(first, second);
        _graph.ValidateTopologyImportEdge(_world, _state, edge, endpoints);
        try
        {
            _generation.ImportEdge(new RelationEdge<T>(edge), endpoints);
        }
        catch (InvalidOperationException exception)
        {
            throw new InvalidDataException(
                $"Invalid serialized relation edge {edge}.",
                exception);
        }
        _graph.AddTopologyImportComponents<T>(_world, edge, endpoints);

        if (_state.Schema.Direction == RelationDirection.Directed)
        {
            Increment(first, RelationAdjacencyRole.Outgoing);
            Increment(second, RelationAdjacencyRole.Incoming);
        }
        else
        {
            Increment(first, RelationAdjacencyRole.Incident);
            if (second != first)
                Increment(second, RelationAdjacencyRole.Incident);
        }
    }

    internal void SetOrderedSequenceCount(int count)
    {
        RequireOpen();
        if (_expectedSequenceCount >= 0)
            throw new InvalidOperationException("Relation ordered sequence count is already set.");
        _expectedSequenceCount = count;
    }

    internal OrderedSequence BeginOrderedSequence(
        Entity endpoint,
        RelationAdjacencyRole role,
        int count)
    {
        RequireOpen();
        if (_expectedSequenceCount < 0)
            throw new InvalidOperationException("Relation ordered sequence count has not been read.");
        if (_sequenceCount >= _expectedSequenceCount)
            throw new InvalidDataException("Relation payload contains more ordered sequences than declared.");
        _graph.ValidateTopologyImportEndpoint(_world, _state.Schema, endpoint, role);

        MembershipPlan plan = GetOrCreate(endpoint, role);
        if (plan.Ordered)
            throw new InvalidDataException($"Duplicate ordered {role} sequence for endpoint {endpoint}.");
        if (count != plan.Count)
        {
            throw new InvalidDataException(
                $"Ordered {role} sequence for {endpoint} has {count} edges but canonical endpoints produce {plan.Count}.");
        }

        return new OrderedSequence(this, endpoint, role, plan, count);
    }

    internal void Complete()
    {
        RequireOpen();
        if (_generation.Edges.Count != _expectedEdgeCount)
        {
            throw new InvalidDataException(
                $"Relation payload declared {_expectedEdgeCount} edges but supplied {_generation.Edges.Count}.");
        }
        if (_expectedSequenceCount < 0 || _sequenceCount != _expectedSequenceCount)
        {
            throw new InvalidDataException(
                $"Relation payload declared {_expectedSequenceCount} ordered sequences but supplied {_sequenceCount}.");
        }

        Dictionary<RelationCanonicalEndpointKey, MembershipPlan> memberships = Memberships;
        foreach (KeyValuePair<RelationCanonicalEndpointKey, MembershipPlan> pair in memberships)
        {
            if (pair.Value.Ordered)
                continue;
            _generation.ImportShard(
                pair.Key.Endpoint,
                pair.Key.Role,
                new RelationAdjacencyEntry<T>[pair.Value.Count],
                RelationAdjacencyOrderPolicy.Unordered);
        }

        foreach (KeyValuePair<Entity, byte> pair in _generation.Edges)
        {
            Entity edge = pair.Key;
            RelationEndpointPair endpoints = RelationEndpointAccess.ReadCurrent<T>(
                _world,
                edge,
                _state.Schema);
            var relationEdge = new RelationEdge<T>(edge);
            if (_state.Schema.Direction == RelationDirection.Directed)
            {
                AppendUnordered(
                    endpoints.First,
                    RelationAdjacencyRole.Outgoing,
                    new RelationAdjacencyEntry<T>(relationEdge, endpoints.Second));
                AppendUnordered(
                    endpoints.Second,
                    RelationAdjacencyRole.Incoming,
                    new RelationAdjacencyEntry<T>(relationEdge, endpoints.First));
            }
            else
            {
                AppendUnordered(
                    endpoints.First,
                    RelationAdjacencyRole.Incident,
                    new RelationAdjacencyEntry<T>(relationEdge, endpoints.Second));
                if (endpoints.Second != endpoints.First)
                {
                    AppendUnordered(
                        endpoints.Second,
                        RelationAdjacencyRole.Incident,
                        new RelationAdjacencyEntry<T>(relationEdge, endpoints.First));
                }
            }
        }

        foreach (KeyValuePair<RelationCanonicalEndpointKey, MembershipPlan> pair in memberships)
        {
            if (!pair.Value.Ordered && pair.Value.Offset != pair.Value.Count)
            {
                throw new InvalidDataException(
                    $"Relation {pair.Key.Role} shard for {pair.Key.Endpoint} did not receive its declared memberships.");
            }
        }

        // Cardinality is validated against the final endpoint-local shard image only after every
        // shard has been filled, but before any marker makes that image externally observable.
        _generation.CompleteTopologyImportCardinality();
        _graph.CompleteTopologyImport(_world, _state, _generation);
        _memberships = null;
        _completed = true;
    }

    private void Increment(Entity endpoint, RelationAdjacencyRole role)
    {
        MembershipPlan plan = GetOrCreate(endpoint, role);
        plan.Count = checked(plan.Count + 1);
    }

    private MembershipPlan GetOrCreate(Entity endpoint, RelationAdjacencyRole role)
    {
        Dictionary<RelationCanonicalEndpointKey, MembershipPlan> memberships = Memberships;
        var key = new RelationCanonicalEndpointKey(endpoint, role);
        if (memberships.TryGetValue(key, out MembershipPlan? plan))
            return plan;
        plan = new MembershipPlan();
        memberships.Add(key, plan);
        return plan;
    }

    private void AppendUnordered(
        Entity endpoint,
        RelationAdjacencyRole role,
        RelationAdjacencyEntry<T> entry)
    {
        MembershipPlan plan = Memberships[new RelationCanonicalEndpointKey(endpoint, role)];
        if (plan.Ordered)
            return;
        RelationAdjacencyShard<T> shard = _generation.GetShard(endpoint, role);
        if ((uint)plan.Offset >= (uint)shard.Entries.Length)
            throw new InvalidDataException("Relation unordered shard exceeded its final allocation.");
        shard.SetEntry(plan.Offset++, entry);
    }

    private Dictionary<RelationCanonicalEndpointKey, MembershipPlan> Memberships =>
        _memberships ?? throw new InvalidOperationException(
            "Relation topology import membership metadata was already released.");

    private void RequireOpen()
    {
        if (_completed)
            throw new InvalidOperationException("Relation topology import is already complete.");
    }

    internal sealed class OrderedSequence
    {
        private readonly RelationTopologyImport<T> _owner;
        private readonly Entity _endpoint;
        private readonly RelationAdjacencyRole _role;
        private readonly MembershipPlan _plan;
        private RelationAdjacencyEntry<T>[]? _entries;
        private HashSet<Entity>? _seen;
        private int _offset;
        private bool _completed;

        internal int DuplicateLookupCount { get; private set; }

        internal int RetainedDuplicateMetadataCount => _seen?.Count ?? 0;

        internal object? PendingBackingIdentity => _entries;

        internal OrderedSequence(
            RelationTopologyImport<T> owner,
            Entity endpoint,
            RelationAdjacencyRole role,
            MembershipPlan plan,
            int count)
        {
            _owner = owner;
            _endpoint = endpoint;
            _role = role;
            _plan = plan;
            _entries = new RelationAdjacencyEntry<T>[count];
            _seen = new HashSet<Entity>(count);
        }

        internal void AddEdge(Entity edge)
        {
            RelationAdjacencyEntry<T>[] entries = _entries ??
                throw new InvalidDataException("Relation ordered sequence is already complete.");
            if (_completed || (uint)_offset >= (uint)entries.Length)
                throw new InvalidDataException("Relation ordered sequence exceeded its declared length.");
            if (!_owner._state.IsEdge(edge))
                throw new InvalidDataException($"Ordered relation sequence contains unknown edge {edge}.");
            DuplicateLookupCount++;
            if (!_seen!.Add(edge))
                throw new InvalidDataException($"Ordered relation sequence repeats edge {edge}.");

            RelationEndpointPair endpoints = RelationEndpointAccess.ReadCurrent<T>(
                _owner._world,
                edge,
                _owner._state.Schema);
            Entity other = _role switch
            {
                RelationAdjacencyRole.Outgoing when endpoints.First == _endpoint => endpoints.Second,
                RelationAdjacencyRole.Incoming when endpoints.Second == _endpoint => endpoints.First,
                RelationAdjacencyRole.Incident when endpoints.First == _endpoint => endpoints.Second,
                RelationAdjacencyRole.Incident when endpoints.Second == _endpoint => endpoints.First,
                _ => throw new InvalidDataException(
                    $"Edge {edge} is not a {_role} membership of endpoint {_endpoint}."),
            };
            entries[_offset++] = new RelationAdjacencyEntry<T>(new RelationEdge<T>(edge), other);
        }

        internal void Complete()
        {
            RelationAdjacencyEntry<T>[] entries = _entries ??
                throw new InvalidDataException("Relation ordered sequence is already complete.");
            if (_completed || _offset != entries.Length)
                throw new InvalidDataException("Relation ordered sequence did not supply its declared memberships.");
            _owner._generation.ImportShard(
                _endpoint,
                _role,
                entries,
                RelationAdjacencyOrderPolicy.Ordered);
            _plan.Ordered = true;
            _owner._sequenceCount++;
            _completed = true;
            _seen = null;
            _entries = null;
        }
    }
}

}

namespace SomeEngine.ECS
{

internal readonly record struct RelationTopologyWriteDiagnostics(
    long WriteCount,
    long EdgeVisits,
    long OrderedShardVisits);

internal sealed class RelationTopologyWriteCounter
{
    private long _exportCount;
    private long _edgeVisits;
    private long _orderedShardVisits;

    internal void Record(int edgeVisits, int orderedShardVisits)
    {
        Interlocked.Increment(ref _exportCount);
        Interlocked.Add(ref _edgeVisits, edgeVisits);
        Interlocked.Add(ref _orderedShardVisits, orderedShardVisits);
    }

    internal RelationTopologyWriteDiagnostics Snapshot() =>
        new(
            Volatile.Read(ref _exportCount),
            Volatile.Read(ref _edgeVisits),
            Volatile.Read(ref _orderedShardVisits));
}

public partial class World
{
    private readonly RelationComponentSlotTable<RelationTopologyWriteCounter>
        _relationTopologyWriteCounters = new();
    private readonly object _relationTopologyWriteCounterRegistrationLock = new();

    internal HierarchyDomainStore<TDomain>.TopologyImport BeginHierarchyTopologyImport<TDomain>(
        int parentCount)
        where TDomain : IHierarchyDomain =>
        _hierarchy.Domain<TDomain>().BeginTopologyImport(parentCount);

    internal RelationTopologyImport<T> BeginRelationTopologyImport<T>(
        RelationDirection direction,
        RelationCardinality cardinality,
        bool allowSelfEdge,
        int edgeCount)
        where T : struct, IComponent =>
        new(
            this,
            _relationGraph,
            direction,
            cardinality,
            allowSelfEdge,
            edgeCount);

    internal void RecordRelationTopologyWrite<T>(int edgeVisits, int orderedShardVisits)
        where T : struct, IComponent
    {
        int componentId = ComponentMetadata<T>.Id;
        if (!_relationTopologyWriteCounters.TryGetValue(componentId, out RelationTopologyWriteCounter? counter))
        {
            lock (_relationTopologyWriteCounterRegistrationLock)
            {
                if (!_relationTopologyWriteCounters.TryGetValue(componentId, out counter))
                {
                    counter = new RelationTopologyWriteCounter();
                    _relationTopologyWriteCounters.Add(componentId, counter);
                }
            }
        }
        counter.Record(edgeVisits, orderedShardVisits);
    }

    internal RelationTopologyWriteDiagnostics GetRelationTopologyWriteDiagnostics<T>()
        where T : struct, IComponent =>
        _relationTopologyWriteCounters.TryGetValue(
            ComponentMetadata<T>.Id,
            out RelationTopologyWriteCounter? counter)
                ? counter.Snapshot()
                : default;

}

}

namespace SomeEngine.ECS.Owners
{

internal sealed partial class RelationGraph
{
    internal RelationTypeState<T>? PrepareSerializationWrite<T>()
        where T : struct, IComponent
    {
        if (_destroyingEdges.Count != 0 ||
            _commandBatchDepth != 0 ||
            _commandBatchPayloads.Count != 0)
        {
            throw new InvalidOperationException(
                "Relation topology cannot be serialized while a command batch or edge destroy is active.");
        }

        if (HasPendingEndpointPreimages())
        {
            throw new InvalidOperationException(
                "Relation topology cannot be serialized while endpoint preimages are pending.");
        }

        if (!_states.TryGetValue(ComponentMetadata<T>.Id, out IRelationTypeState? existing))
            return null;

        var state = (RelationTypeState<T>)existing;
        return state;
    }

    internal RelationTypeState<T> BeginTopologyImport<T>(World world)
        where T : struct, IComponent
    {
        ArgumentNullException.ThrowIfNull(world);
        if (_destroyingEdges.Count != 0 ||
            _commandBatchDepth != 0 ||
            _commandBatchPayloads.Count != 0)
        {
            throw new InvalidDataException(
                "Relation topology import requires an idle, new World relation graph.");
        }
        if (HasPendingEndpointPreimages())
            throw new InvalidDataException("Relation topology import found pending endpoint preimages.");
        return State<T>();
    }

    internal void ValidateTopologyImportEdge<T>(
        World world,
        RelationTypeState<T> state,
        Entity edge,
        RelationEndpointPair endpoints)
        where T : struct, IComponent
    {
        if (!world.IsAlive(edge) || world.IsPendingCleanup(edge) || !world.Has<T>(edge))
        {
            throw new InvalidDataException(
                $"Serialized relation edge {edge} has no live entity carrying payload {typeof(T).FullName}.");
        }
        foreach (IRelationTypeState existing in _states)
        {
            if (existing.IsEdge(edge))
                throw new InvalidDataException($"Entity {edge} is already a registered relation edge.");
        }
        try
        {
            ValidateEndpoints(world, state.Schema, endpoints.First, endpoints.Second);
        }
        catch (InvalidOperationException exception)
        {
            throw new InvalidDataException($"Invalid endpoints for serialized relation edge {edge}.", exception);
        }
    }

    internal void ValidateTopologyImportEndpoint(
        World world,
        RelationSchema schema,
        Entity endpoint,
        RelationAdjacencyRole role)
    {
        try
        {
            ValidateEndpoint(world, endpoint, "ordered adjacency");
        }
        catch (InvalidOperationException exception)
        {
            throw new InvalidDataException($"Invalid ordered relation endpoint {endpoint}.", exception);
        }

        bool valid = schema.Direction switch
        {
            RelationDirection.Directed =>
                role is RelationAdjacencyRole.Outgoing or RelationAdjacencyRole.Incoming,
            RelationDirection.Undirected => role == RelationAdjacencyRole.Incident,
            _ => false,
        };
        if (!valid)
        {
            throw new InvalidDataException(
                $"Adjacency role {role} is invalid for {schema.Direction} relation topology.");
        }
    }

    internal void AddTopologyImportComponents<T>(
        World world,
        Entity edge,
        RelationEndpointPair endpoints)
        where T : struct, IComponent
    {
        AddCanonicalEndpoints<T>(
            world,
            edge,
            RelationSchema.For<T>(),
            endpoints.First,
            endpoints.Second);
        world.Components.Add(edge, new AppliedRelationEndpoints<T>
        {
            EndpointA = endpoints.First,
            EndpointB = endpoints.Second,
            IsApplied = true,
        });
    }

    internal void CompleteTopologyImport<T>(
        World world,
        RelationTypeState<T> state,
        RelationGeneration<T> generation)
        where T : struct, IComponent
    {
        if (state.Schema.Direction == RelationDirection.Directed)
        {
            foreach (KeyValuePair<Entity, RelationAdjacencyShard<T>> pair in generation.Outgoing)
            {
                RelationAdjacencyShard<T> shard = pair.Value;
                WriteMarker(
                    world,
                    pair.Key,
                    new Outgoing<T>(shard.Entries.Length, generation.Id),
                    shouldExist: true);
            }
            foreach (KeyValuePair<Entity, RelationAdjacencyShard<T>> pair in generation.Incoming)
            {
                RelationAdjacencyShard<T> shard = pair.Value;
                WriteMarker(
                    world,
                    pair.Key,
                    new Incoming<T>(shard.Entries.Length, generation.Id),
                    shouldExist: true);
            }
        }
        else
        {
            foreach (KeyValuePair<Entity, RelationAdjacencyShard<T>> pair in generation.Incident)
            {
                RelationAdjacencyShard<T> shard = pair.Value;
                WriteMarker(
                    world,
                    pair.Key,
                    new Incident<T>(shard.Entries.Length, generation.Id),
                    shouldExist: true);
            }
        }
    }

}

}
