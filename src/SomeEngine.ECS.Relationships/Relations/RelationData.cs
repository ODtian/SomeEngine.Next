using System.Buffers;
using System.Runtime.InteropServices;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Storage;

namespace SomeEngine.ECS.Relations;

internal readonly record struct AdjacencyKey(Entity Entity, RelationAdjacencyRole Role);

internal sealed partial class RelationData<T> : IWorldLifecycle where T : struct
{
    public bool RequiresIndividualDestroy(Archetype archetype) => true;

    private readonly World _world;
    private readonly RelationSchema _schema;
    private readonly bool _directed, _uniqueSource, _uniqueTarget, _uniquePair;
    private AdjacencyHead[] _outgoing, _incoming;
    private AppliedEdge[] _applied;
    private EndpointClaim[] _sourceClaims, _targetClaims;
    private readonly Dictionary<EndpointPair, Entity> _pairs = new();
    private readonly Dictionary<AdjacencyKey, AdjacencyHead> _retiredHeads = new();
    private readonly Dictionary<Entity, AppliedEdge> _retiredApplied = new();
    private readonly DifferenceTargets _sourceDifferences, _targetDifferences;
    private readonly DestroyedReader _reader;
    private readonly Query _main, _changes, _removed;
    private readonly HashSet<Entity> _maintenanceChanges = new();
    private ulong _version;
    private bool _hasDeferredChanges;

    internal RelationData(World world)
    {
        RelationSerializationType<T>.Register();
        _world = world;
        _schema = RelationSchema.For<T>();
        _directed = _schema.Direction == RelationDirection.Directed;
        _uniqueSource = _schema.Cardinality is RelationCardinality.UniqueSource or RelationCardinality.OneToOne;
        _uniqueTarget = _schema.Cardinality is RelationCardinality.UniqueTarget or RelationCardinality.OneToOne;
        _uniquePair = _schema.Cardinality == RelationCardinality.UniquePair;
        int capacity = world.Records.Length;
        _outgoing = new AdjacencyHead[capacity];
        _incoming = _directed ? new AdjacencyHead[capacity] : [];
        _applied = new AppliedEdge[capacity];
        _sourceClaims = _uniqueSource ? new EndpointClaim[capacity] : [];
        _targetClaims = _directed && _uniqueTarget ? new EndpointClaim[capacity] : [];
        _sourceDifferences = new(capacity);
        _targetDifferences = new(capacity);
        _reader = world.WatchDestroyed();
        _main = world.Query<RelationEndpoints<T>>();
        _changes = world.Query(new QueryDescription().WithChanged<RelationEndpoints<T>>(0));
        _removed = world.Query<Removed<RelationEndpoints<T>>>();
    }

    private bool Active(Entity entity) => _world.IsAlive(entity) && !World.HasDestroyed(in _world.Records[entity.Id]);

    private void RequireActive(Entity entity)
    {
        if (!Active(entity)) throw new ArgumentException("The relation requires an active entity.");
    }

    private bool ReadMain(Entity edge, out RelationEndpoints<T> value)
    {
        if (_world.IsAlive(edge))
        {
            ref EntityRecord record = ref _world.Records[edge.Id];
            ColumnEntry[] columns = record.Chunk!.Columns;
            int type = ComponentType<RelationEndpoints<T>>.TypeId;
            if ((uint)type < (uint)columns.Length && columns[type].Has(record.Row))
            {
                value = ColumnEntry.At<RelationEndpoints<T>>(ref columns[type], record.Row);
                return true;
            }
        }
        value = default;
        return false;
    }

    private void EnsureCapacity()
    {
        int capacity = _world.Records.Length;
        if (_applied.Length >= capacity) return;
        Array.Resize(ref _applied, capacity);
        Array.Resize(ref _outgoing, capacity);
        if (_directed) Array.Resize(ref _incoming, capacity);
        if (_uniqueSource) Array.Resize(ref _sourceClaims, capacity);
        if (_directed && _uniqueTarget) Array.Resize(ref _targetClaims, capacity);
        _sourceDifferences.Reserve(capacity);
        _targetDifferences.Reserve(capacity);
    }

    private void PrepareApplied(Entity edge)
    {
        EnsureCapacity();
        ref AppliedEdge value = ref _applied[edge.Id];
        if (value.Generation == edge.Generation) return;
        if (value.Source != Entity.Null) _retiredApplied[new(edge.Id, value.Generation)] = value;
        value = new() { Generation = edge.Generation };
    }

    private AppliedEdge Applied(Entity edge)
        => (uint)edge.Id < (uint)_applied.Length && _applied[edge.Id].Generation == edge.Generation
            ? _applied[edge.Id] : _retiredApplied.GetValueOrDefault(edge);

    private ref AppliedEdge AppliedRow(Entity edge)
    {
        if (_applied[edge.Id].Generation == edge.Generation) return ref _applied[edge.Id];
        return ref CollectionsMarshal.GetValueRefOrNullRef(_retiredApplied, edge);
    }

    private void WriteApplied(Entity edge, in AppliedEdge value)
    {
        if (_applied[edge.Id].Generation == edge.Generation) _applied[edge.Id] = value;
        else if (value.Source == Entity.Null) _retiredApplied.Remove(edge);
        else _retiredApplied[edge] = value;
    }

    private ref AdjacencyHead CurrentHead(Entity entity, RelationAdjacencyRole role)
    {
        if (role == RelationAdjacencyRole.Incoming) return ref _incoming[entity.Id];
        return ref _outgoing[entity.Id];
    }

    private ref AdjacencyHead PrepareHead(Entity entity, RelationAdjacencyRole role)
    {
        ref AdjacencyHead head = ref CurrentHead(entity, role);
        if (head.Generation == entity.Generation) return ref head;
        if (head.Items is not null) _retiredHeads[new(new(entity.Id, head.Generation), role)] = head;
        head = new() { Generation = entity.Generation };
        return ref head;
    }

    private AdjacencyHead ReadHead(Entity entity, RelationAdjacencyRole role)
    {
        AdjacencyHead[] heads = role == RelationAdjacencyRole.Incoming ? _incoming : _outgoing;
        return (uint)entity.Id < (uint)heads.Length && heads[entity.Id].Generation == entity.Generation
            ? heads[entity.Id] : _retiredHeads.GetValueOrDefault(new(entity, role));
    }

    private ref AdjacencyHead HeadRow(Entity entity, RelationAdjacencyRole role)
    {
        ref AdjacencyHead head = ref CurrentHead(entity, role);
        if (head.Generation == entity.Generation) return ref head;
        return ref CollectionsMarshal.GetValueRefOrNullRef(_retiredHeads, new(entity, role));
    }

    private void ReserveHead(Entity entity, RelationAdjacencyRole role, int required)
    {
        ref AdjacencyHead head = ref PrepareHead(entity, role);
        if (required <= (head.Items?.Length ?? 0)) return;
        var items = AdjacencyHead.Rent(required);
        head.Entries.CopyTo(items);
        if (head.Items is { } previous) AdjacencyHead.Return(previous);
        head.Items = items;
    }

    private int Append(Entity endpoint, Entity other, Entity edge, RelationAdjacencyRole role)
    {
        ref AdjacencyHead head = ref HeadRow(endpoint, role);
        int place = head.Count;
        head.Items![place] = new(other, edge);
        head.SetCount(place + 1);
        return place;
    }

    private void WritePlace(Entity edge, Entity endpoint, RelationAdjacencyRole role, int place)
    {
        ref AppliedEdge value = ref AppliedRow(edge);
        WritePlace(ref value, endpoint, role, place);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static void WritePlace(ref AppliedEdge value, Entity endpoint, RelationAdjacencyRole role, int place)
    {
        if (role == RelationAdjacencyRole.Outgoing) value.SourcePlace = place;
        else if (role == RelationAdjacencyRole.Incoming) value.TargetPlace = place;
        else
        {
            if (value.Source == endpoint) value.SourcePlace = place;
            if (value.Target == endpoint) value.TargetPlace = place;
        }
    }

    private void Drop(Entity endpoint, int place, RelationAdjacencyRole role, bool keepEmpty = false)
    {
        ref AdjacencyHead head = ref HeadRow(endpoint, role);
        int last = head.Count - 1;
        if (place != last)
        {
            if (head.IsOrdered)
            {
                Array.Copy(head.Items!, place + 1, head.Items!, place, last - place);
                for (int i = place; i < last; i++) WritePlace(head.Items![i].Edge, endpoint, role, i);
            }
            else
            {
                RelationEntry moved = head.Items![last];
                head.Items[place] = moved;
                WritePlace(moved.Edge, endpoint, role, place);
            }
        }
        head.SetCount(last);
        if (last != 0 || keepEmpty) return;
        AdjacencyHead.Return(head.Items!);
        head.Items = null;
        if (CurrentHead(endpoint, role).Generation != endpoint.Generation) _retiredHeads.Remove(new(endpoint, role));
    }
}
