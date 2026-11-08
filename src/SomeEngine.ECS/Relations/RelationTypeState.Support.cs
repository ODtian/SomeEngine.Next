using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Owners;

namespace SomeEngine.ECS.Relations;

internal readonly record struct RelationEndpointPair(Entity First, Entity Second);

internal readonly record struct RelationAppliedEndpointImage(
    RelationEndpointPair Endpoints,
    bool IsApplied);

internal readonly record struct RelationPendingPlacement(
    int? FirstInsertIndex,
    int? SecondInsertIndex,
    long Sequence = 0);

internal readonly record struct RelationEndpointTransition<T>(
    RelationEdge<T> Edge,
    RelationEndpointPair Applied,
    RelationEndpointPair Current,
    int? FirstInsertIndex = null,
    int? SecondInsertIndex = null,
    bool HasAppliedMembership = true)
    where T : struct, IComponent;

internal readonly record struct RelationAffectedShard(
    Entity Endpoint,
    RelationAdjacencyRole Role);

internal readonly record struct RelationAdjacencyBatchDiagnostics(
    long SourceEntryCopies,
    long FrozenEntries,
    long FrozenShards);

internal readonly record struct RelationCommandBatchValidationDiagnostics(
    long FullScanCount,
    long TransitionVisitCount);

internal readonly record struct RelationCanonicalLookupDiagnostics(
    long BulkIndexBuildCount,
    long BulkIndexBuildEdgeVisits,
    long BetweenLookupCount,
    long BetweenBucketVisits,
    long AtLookupCount,
    long AtBucketVisits,
    long CleanupLookupCount,
    long CleanupAppliedEntryVisits,
    long CleanupDirtyEntryVisits);

internal readonly record struct RelationCanonicalEndpointKey(
    Entity Endpoint,
    RelationAdjacencyRole Role);

internal sealed class RelationAdjacencyBatchDiagnosticCounter
{
    private long _sourceEntryCopies;
    private long _frozenEntries;
    private long _frozenShards;

    private RelationAdjacencyBatchDiagnosticCounter(
        long sourceEntryCopies,
        long frozenEntries,
        long frozenShards)
    {
        _sourceEntryCopies = sourceEntryCopies;
        _frozenEntries = frozenEntries;
        _frozenShards = frozenShards;
    }

    internal RelationAdjacencyBatchDiagnosticCounter()
    {
    }

    internal void RecordSourceCopy(int count) => _sourceEntryCopies += count;

    internal void RecordFreeze(int count)
    {
        _frozenEntries += count;
        _frozenShards++;
    }

    internal RelationAdjacencyBatchDiagnostics Snapshot() => new(
        _sourceEntryCopies,
        _frozenEntries,
        _frozenShards);

    internal RelationAdjacencyBatchDiagnosticCounter CloneDetached() => new(
        _sourceEntryCopies,
        _frozenEntries,
        _frozenShards);
}

internal readonly struct RelationAffectedShards
{
    private readonly RelationAffectedShard _first, _second;
    private readonly RelationAffectedShard[]? _entries;

    internal RelationAffectedShards(RelationAffectedShard first)
    {
        _first = first;
        Length = 1;
    }

    internal RelationAffectedShards(RelationAffectedShard first, RelationAffectedShard second)
    {
        _first = first; _second = second;
        Length = 2;
    }

    private RelationAffectedShards(RelationAffectedShard[] entries)
    {
        _entries = entries;
        Length = entries.Length;
    }

    internal int Length { get; }
    internal RelationAffectedShard this[int index] =>
        _entries is not null ? _entries[index] : index == 0 ? _first : _second;
    public static implicit operator RelationAffectedShards(RelationAffectedShard[] entries) => new(entries);
}

internal readonly struct PreparedRelationState<T>
    where T : struct, IComponent
{
    private readonly RelationAffectedShards _affectedShards;

    internal PreparedRelationState(
        RelationGeneration<T> previous,
        RelationGeneration<T> next,
        RelationAffectedShards affectedShards,
        bool hasChanges = true)
    {
        Previous = previous;
        Next = next;
        _affectedShards = affectedShards;
        HasChanges = hasChanges;
    }

    internal RelationGeneration<T> Previous { get; }

    internal RelationGeneration<T> Next { get; }

    internal RelationAffectedShards AffectedShards =>
        _affectedShards;

    internal bool HasChanges { get; }
}

internal interface IRelationTypeState
{
    Type PayloadType { get; }

    IRelationTypeState CloneDetached();

    void DestroyIncidentEdges(
        RelationGraph graph,
        World world,
        Entity endpoint,
        ExceptionAccumulator faults);

    bool IsEdge(Entity entity);

    void DropEndpointState(Entity endpoint);

    bool HasEndpointState(Entity endpoint);

    void DestroyEdge(
        RelationGraph graph,
        World world,
        Entity edge,
        bool destroyEntity);

    void BeginCommandBatch();

    void EndCommandBatch(bool completed);
}

internal abstract class RelationAdjacencyShard<T>
    where T : struct, IComponent
{
    private readonly RelationAdjacencyEntry<T>[] _entries;

    protected RelationAdjacencyShard(RelationAdjacencyEntry<T>[] ownedEntries)
    {
        _entries = ownedEntries;
    }

    internal abstract RelationAdjacencyOrderPolicy Policy { get; }

    internal ReadOnlySpan<RelationAdjacencyEntry<T>> Entries => _entries;

    internal ReadOnlyMemory<RelationAdjacencyEntry<T>> EntryMemory => _entries;

    internal void SetEntry(int index, RelationAdjacencyEntry<T> entry) =>
        _entries[index] = entry;
}

/// <summary>
/// One command-batch-local adjacency editor. It copies an immutable shard at most once, applies
/// packed unordered edits in O(1) and freezes one immutable array at publication.
/// </summary>
internal sealed class MutableRelationAdjacencyShard<T>
    where T : struct, IComponent
{
    private const int LinearSearchLimit = 8;
    private List<RelationAdjacencyEntry<T>>? _entries;
    private RelationAdjacencyEntry<T> _first, _second;
    private int _smallCount;
    private Dictionary<Entity, int>? _indices;
    private readonly TopologyOrderDiagnosticCounter _orderDiagnostics;

    internal MutableRelationAdjacencyShard(
        RelationAdjacencyShard<T> source,
        TopologyOrderDiagnosticCounter orderDiagnostics)
    {
        Policy = source.Policy;
        _orderDiagnostics = orderDiagnostics;
        ReadOnlySpan<RelationAdjacencyEntry<T>> entries = source.Entries;
        if (Policy == RelationAdjacencyOrderPolicy.Unordered && entries.Length <= 2)
        {
            if (entries.Length > 0) _first = entries[0];
            if (entries.Length > 1) _second = entries[1];
            _smallCount = entries.Length;
        }
        else
        {
            _entries = new List<RelationAdjacencyEntry<T>>(Math.Max(2, entries.Length));
            for (int index = 0; index < entries.Length; index++) _entries.Add(entries[index]);
        }
        if (Policy == RelationAdjacencyOrderPolicy.Ordered || Count > LinearSearchLimit) CreateIndices();
    }

    internal int Count => _entries?.Count ?? _smallCount;

    internal RelationAdjacencyOrderPolicy Policy { get; private set; }

    internal bool Contains(RelationEdge<T> edge) => FindIndex(edge.Entity) >= 0;

    internal void Add(RelationAdjacencyEntry<T> entry, int? insertIndex)
    {
        if (FindIndex(entry.Edge.Entity) >= 0)
            throw new InvalidOperationException($"Edge {entry.Edge.Entity} already exists in adjacency.");

        if (Policy == RelationAdjacencyOrderPolicy.Unordered)
        {
            if (insertIndex is not null)
                throw new InvalidOperationException("Unordered relation adjacency does not accept an index.");
            if (_entries is null && _smallCount < 2)
            {
                if (_smallCount == 0) _first = entry;
                else _second = entry;
                _smallCount++;
            }
            else
            {
                EnsureEntries();
                _entries!.Add(entry);
            }
            if (_indices is not null) _indices.Add(entry.Edge.Entity, Count - 1);
            else if (Count > LinearSearchLimit) CreateIndices();
            return;
        }

        _orderDiagnostics.RecordOrderedPath();
        int index = insertIndex ?? Count;
        if ((uint)index > (uint)Count)
            throw new ArgumentOutOfRangeException(nameof(insertIndex));
        _entries!.Insert(index, entry);
        RefreshIndices(index);
    }

    internal void Remove(RelationEdge<T> edge)
    {
        int index = FindIndex(edge.Entity);
        if (index < 0)
            throw new InvalidOperationException($"Adjacency does not contain edge {edge.Entity}.");
        _indices?.Remove(edge.Entity);

        if (Policy == RelationAdjacencyOrderPolicy.Unordered)
        {
            int last = Count - 1;
            if (_entries is null)
            {
                if (index == 0) _first = Entry(last);
                if (last == 0) _first = default;
                else _second = default;
                _smallCount--;
                return;
            }
            if (index != last)
            {
                RelationAdjacencyEntry<T> moved = _entries[last];
                _entries[index] = moved;
                if (_indices is not null) _indices[moved.Edge.Entity] = index;
            }
            _entries.RemoveAt(last);
            return;
        }

        _orderDiagnostics.RecordOrderedPath();
        _entries!.RemoveAt(index);
        RefreshIndices(index);
    }

    internal bool Reorder(RelationEdge<T> edge, int insertIndex)
    {
        if (Policy != RelationAdjacencyOrderPolicy.Ordered)
            throw new InvalidOperationException("Relation adjacency is not ordered.");
        int oldIndex = FindIndex(edge.Entity);
        if (oldIndex < 0)
            throw new InvalidOperationException($"Adjacency does not contain edge {edge.Entity}.");
        if ((uint)insertIndex >= (uint)Count)
            throw new ArgumentOutOfRangeException(nameof(insertIndex));
        if (oldIndex == insertIndex)
            return false;

        _orderDiagnostics.RecordOrderedPath();
        RelationAdjacencyEntry<T> entry = _entries![oldIndex];
        _entries.RemoveAt(oldIndex);
        _entries.Insert(insertIndex, entry);
        RefreshIndices(Math.Min(oldIndex, insertIndex));
        return true;
    }

    internal void SetPolicy(RelationAdjacencyOrderPolicy policy)
    {
        if (Policy == policy)
            return;
        Policy = policy;
        if (policy == RelationAdjacencyOrderPolicy.Ordered)
        {
            EnsureEntries();
            if (_indices is null) CreateIndices();
            _orderDiagnostics.RecordOrderedPath();
            _entries!.Sort(static (left, right) =>
            {
                int index = left.Edge.Entity.Index.CompareTo(right.Edge.Entity.Index);
                return index != 0
                    ? index
                    : left.Edge.Entity.Generation.CompareTo(right.Edge.Entity.Generation);
            });
            RefreshIndices(0);
        }
    }

    internal RelationAdjacencyShard<T>? Freeze()
    {
        if (Count == 0 && Policy == RelationAdjacencyOrderPolicy.Unordered) return null;
        RelationAdjacencyEntry<T>[] entries;
        if (_entries is null)
        {
            entries = new RelationAdjacencyEntry<T>[_smallCount];
            if (_smallCount > 0) entries[0] = _first;
            if (_smallCount > 1) entries[1] = _second;
        }
        else entries = _entries.ToArray();
        return Policy == RelationAdjacencyOrderPolicy.Ordered
            ? new OrderedRelationAdjacencyShard<T>(entries)
            : new UnorderedRelationAdjacencyShard<T>(entries);
    }

    internal RelationAdjacencyShard<T> Snapshot() =>
        Freeze() ?? new UnorderedRelationAdjacencyShard<T>(Array.Empty<RelationAdjacencyEntry<T>>());

    private void RefreshIndices(int start)
    {
        _orderDiagnostics.RecordOrderedIndexWork(Count - start);
        for (int i = start; i < Count; i++)
            _indices![Entry(i).Edge.Entity] = i;
    }

    private int FindIndex(Entity edge)
    {
        if (_indices is not null) return _indices.TryGetValue(edge, out int position) ? position : -1;
        for (int index = 0; index < Count; index++)
            if (Entry(index).Edge.Entity == edge) return index;
        return -1;
    }

    private void CreateIndices()
    {
        _indices = new Dictionary<Entity, int>(Count);
        for (int index = 0; index < Count; index++) _indices.Add(Entry(index).Edge.Entity, index);
    }

    private RelationAdjacencyEntry<T> Entry(int index) =>
        _entries is not null ? _entries[index] : index == 0 ? _first : _second;

    private void EnsureEntries()
    {
        if (_entries is not null) return;
        _entries = new List<RelationAdjacencyEntry<T>>(4);
        if (_smallCount > 0) _entries.Add(_first);
        if (_smallCount > 1) _entries.Add(_second);
        _first = default; _second = default; _smallCount = 0;
    }
}

/// <summary>
/// An unordered relation shard stores only packed members. It has no placement,
/// sequence, per-member order key, or ordered-maintenance state.
/// </summary>
internal sealed class UnorderedRelationAdjacencyShard<T> : RelationAdjacencyShard<T>
    where T : struct, IComponent
{
    internal UnorderedRelationAdjacencyShard(RelationAdjacencyEntry<T>[] ownedEntries)
        : base(ownedEntries)
    {
    }

    internal override RelationAdjacencyOrderPolicy Policy =>
        RelationAdjacencyOrderPolicy.Unordered;
}

internal sealed class OrderedRelationAdjacencyShard<T> : RelationAdjacencyShard<T>
    where T : struct, IComponent
{
    internal OrderedRelationAdjacencyShard(RelationAdjacencyEntry<T>[] ownedEntries)
        : base(ownedEntries)
    {
    }

    internal override RelationAdjacencyOrderPolicy Policy =>
        RelationAdjacencyOrderPolicy.Ordered;
}

internal readonly struct RelationPairKey : IEquatable<RelationPairKey>
{
    private RelationPairKey(Entity first, Entity second)
    {
        First = first;
        Second = second;
    }

    private Entity First { get; }

    private Entity Second { get; }

    internal static RelationPairKey Create(
        RelationEndpointPair endpoints,
        RelationDirection direction)
    {
        if (direction == RelationDirection.Directed ||
            CompareEntities(endpoints.First, endpoints.Second) <= 0)
        {
            return new RelationPairKey(endpoints.First, endpoints.Second);
        }

        return new RelationPairKey(endpoints.Second, endpoints.First);
    }

    public bool Equals(RelationPairKey other) =>
        First == other.First && Second == other.Second;

    public override bool Equals(object? obj) =>
        obj is RelationPairKey other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(First, Second);

    private static int CompareEntities(Entity left, Entity right)
    {
        int index = left.Index.CompareTo(right.Index);
        return index != 0 ? index : left.Generation.CompareTo(right.Generation);
    }
}

internal static class RelationEndpointAccess
{
    internal static bool HasCurrent<T>(
        Owners.Components components,
        Entity edge,
        RelationSchema schema)
        where T : struct, IComponent =>
        schema.Direction == RelationDirection.Directed
            ? components.Has<DirectedRelationEndpoints<T>>(edge)
            : components.Has<UndirectedRelationEndpoints<T>>(edge);

    internal static bool HasCurrent<T>(
        World world,
        Entity edge,
        RelationSchema schema)
        where T : struct, IComponent =>
        HasCurrent<T>(world.Components, edge, schema);

    internal static RelationEndpointPair ReadCurrent<T>(
        Owners.Components components,
        Entity edge,
        RelationSchema schema)
        where T : struct, IComponent
    {
        if (schema.Direction == RelationDirection.Directed)
        {
            ref readonly DirectedRelationEndpoints<T> endpoints =
                ref components.ReadRef<DirectedRelationEndpoints<T>>(edge);
            return new RelationEndpointPair(endpoints.Source, endpoints.Target);
        }

        ref readonly UndirectedRelationEndpoints<T> undirected =
            ref components.ReadRef<UndirectedRelationEndpoints<T>>(edge);
        return new RelationEndpointPair(undirected.EndpointA, undirected.EndpointB);
    }

    internal static RelationEndpointPair ReadCurrent<T>(
        World world,
        Entity edge,
        RelationSchema schema)
        where T : struct, IComponent
        => ReadCurrent<T>(world.Components, edge, schema);

    internal static RelationEndpointPair ReadApplied<T>(World world, Entity edge)
        where T : struct, IComponent
    {
        var endpoints = world.Components.Read<AppliedRelationEndpoints<T>>(edge);
        return new RelationEndpointPair(endpoints.EndpointA, endpoints.EndpointB);
    }

    internal static RelationAppliedEndpointImage ReadAppliedImage<T>(World world, Entity edge)
        where T : struct, IComponent
        => ReadAppliedImage<T>(world.Components, edge);

    internal static RelationAppliedEndpointImage ReadAppliedImage<T>(
        Owners.Components components,
        Entity edge)
        where T : struct, IComponent
    {
        ref readonly AppliedRelationEndpoints<T> endpoints =
            ref components.ReadRef<AppliedRelationEndpoints<T>>(edge);
        return new RelationAppliedEndpointImage(
            new RelationEndpointPair(endpoints.EndpointA, endpoints.EndpointB),
            endpoints.IsApplied);
    }
}
