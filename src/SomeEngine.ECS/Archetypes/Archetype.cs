using System.Runtime.InteropServices;
using SomeEngine.ECS.Collections;
using SomeEngine.ECS.Registry;

namespace SomeEngine.ECS.Archetypes;

public class Archetype
{
    private static long s_nextPersistentIdentity;
    private readonly int[] _componentIds;
    private readonly int[] _tableComponentIds;
    private readonly int[] _tagIds;
    private readonly ComponentOperations[] _columnOperations;
    private readonly int[] _enableableComponentIds;
    private readonly int[] _enableableColumnIndices;
    private readonly int[] _sharedComponentIds;
    private readonly int[] _cleanupComponentIds;
    private readonly Dictionary<SharedComponentTuple, SharedChunkBucket> _sharedChunkBuckets =
        new(SharedComponentTupleComparer.Instance);
    private readonly List<Chunk> _chunks = new();
    private readonly Dictionary<int, StructuralTransition> _addTransitions = new();
    private readonly Dictionary<int, StructuralTransition> _removeTransitions = new();
    private readonly Dictionary<SortedValueKey, StructuralTransition> _includeTransitions =
        new(SortedValueComparer.Instance);

    internal long PersistentIdentity { get; }
    public int ArchetypeId { get; }
    public ReadOnlySpan<int> ComponentIds => _componentIds;
    public ReadOnlySpan<int> TableComponentIds => _tableComponentIds;
    public ReadOnlySpan<int> TagIds => _tagIds;
    public uint TypeIdHash { get; }
    internal ReadOnlySpan<ComponentOperations> ColumnOperations => _columnOperations;
    public ReadOnlySpan<int> EnableableComponentIds => _enableableComponentIds;
    public ReadOnlySpan<int> EnableableColumnIndices => _enableableColumnIndices;
    public int MaxChunkRows { get; }
    internal int InitialChunkRows { get; }
    internal int NextChunkRows { get; set; }
    internal int ChunkRowPayloadBytes { get; }
    internal int ChunkFixedPayloadBytes { get; }

    /// <summary>SharedComponent 类型的 componentId 子集（从 TagIds 中筛选）。</summary>
    public ReadOnlySpan<int> SharedComponentIds => _sharedComponentIds;

    /// <summary>
    /// Zero-copy read borrow of the owner's current chunk sequence. Readers must not retain this
    /// span across a structural publication.
    /// </summary>
    internal ReadOnlySpan<Chunk> Chunks => CollectionsMarshal.AsSpan(_chunks);

    internal int ChunkCount => _chunks.Count;
    internal int ChunkListCapacity => _chunks.Capacity;

    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal Chunk ChunkAt(int index) => _chunks[index];

    internal void EnsureChunkListCapacity(int capacity)
    {
        if (capacity > _chunks.Capacity)
            _chunks.Capacity = capacity;
    }

    internal void AddChunk(Chunk chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        _chunks.Add(chunk);
    }

    internal void ReplaceChunk(int index, Chunk chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        _chunks[index] = chunk;
    }

    internal Chunk RemoveLastChunk()
    {
        int last = _chunks.Count - 1;
        Chunk removed = _chunks[last];
        _chunks.RemoveAt(last);
        return removed;
    }

    /// <summary>
    /// O(1) 分配提示：第一个可能非满 chunk 的索引。
    /// Maintained by allocation and chunk recycling paths.
    /// </summary>
    internal int FirstOpenChunk { get; set; }
    internal bool HasCleanupTransition { get; set; }
    internal StructuralTransition CleanupTransition { get; set; }
    internal bool HasCleanupComponents { get; }
    internal ReadOnlySpan<int> CleanupComponentIds => _cleanupComponentIds;

    internal int AddTransitionCount => _addTransitions.Count;
    internal int RemoveTransitionCount => _removeTransitions.Count;
    internal int IncludeTransitionCount => _includeTransitions.Count;
    internal int SharedChunkBucketCount => _sharedChunkBuckets.Count;

    internal bool TryGetAddTransition(
        int componentId,
        out StructuralTransition transition) =>
        _addTransitions.TryGetValue(componentId, out transition);

    internal bool TryGetRemoveTransition(
        int componentId,
        out StructuralTransition transition) =>
        _removeTransitions.TryGetValue(componentId, out transition);

    internal void CacheAddTransition(int componentId, StructuralTransition transition) =>
        _addTransitions[componentId] = transition;

    internal void CacheRemoveTransition(int componentId, StructuralTransition transition) =>
        _removeTransitions[componentId] = transition;

    internal bool TryGetIncludeTransition(
        ReadOnlySpan<int> componentIds,
        out StructuralTransition transition)
    {
        var lookup = _includeTransitions.GetAlternateLookup<ReadOnlySpan<int>>();
        return lookup.TryGetValue(componentIds, out transition);
    }

    internal void CacheIncludeTransition(
        ReadOnlySpan<int> componentIds,
        StructuralTransition transition) =>
        _includeTransitions.Add(new SortedValueKey(componentIds), transition);

    internal bool TryGetSharedChunkBucket(
        ReadOnlySpan<int> sharedValues,
        out SharedChunkBucket bucket)
    {
        var lookup = _sharedChunkBuckets.GetAlternateLookup<ReadOnlySpan<int>>();
        return lookup.TryGetValue(sharedValues, out bucket!);
    }

    internal bool TryGetSharedChunkBucket(
        SharedComponentTuple key,
        out SharedChunkBucket bucket) =>
        _sharedChunkBuckets.TryGetValue(key, out bucket!);

    internal SharedChunkBucket GetOrAddSharedChunkBucket(SharedComponentTuple key)
    {
        if (_sharedChunkBuckets.TryGetValue(key, out SharedChunkBucket? bucket))
            return bucket;

        bucket = new SharedChunkBucket(key);
        _sharedChunkBuckets.Add(key, bucket);
        return bucket;
    }

    internal bool RemoveSharedChunkBucket(SharedComponentTuple key) =>
        _sharedChunkBuckets.Remove(key);

    internal SharedChunkBucket GetOnlySharedChunkBucket()
    {
        if (_sharedChunkBuckets.Count != 1)
        {
            throw new InvalidOperationException(
                $"Expected exactly one shared chunk bucket, found {_sharedChunkBuckets.Count}.");
        }

        foreach (SharedChunkBucket bucket in _sharedChunkBuckets.Values)
            return bucket;

        throw new InvalidOperationException("Shared chunk bucket count changed during access.");
    }

    private const int StartChunkLogicalPayloadBytes = 8 * 1024;

    // Structural transactions fork chunk shells and detach a touched chunk on first write.
    // A multi-megabyte chunk therefore turns an otherwise local mutation (for example spawning
    // one entity) into a multi-megabyte copy. Keep the growth policy for amortized bulk creation,
    // but cap the independently detachable arrays' logical element payload. CLR object/array
    // headers are runtime-specific, so this is deliberately not a physical allocation-byte cap.
    private const int MaxChunkLogicalPayloadBytes = 64 * 1024;
    private const int EntitySize = 8;

    internal Archetype(int archetypeId, ReadOnlySpan<int> sortedComponentIds)
    {
        PersistentIdentity = Interlocked.Increment(ref s_nextPersistentIdentity);
        ArchetypeId = archetypeId;
        _componentIds = sortedComponentIds.ToArray();
        TypeIdHash = StableHash.Compute(sortedComponentIds);

        var tableList = new List<int>();
        var tagList = new List<int>();
        var enableableComponentIds = new List<int>();
        var enableableColumnIndices = new List<int>();
        var cleanupComponentIds = new List<int>();
        var sharedComponentIds = new List<int>();

        foreach (int id in sortedComponentIds)
        {
            ref readonly ComponentInfo info = ref ComponentRegistry.Get(id);
            if (info.Storage == StoragePath.Tag || info.Storage == StoragePath.Shared)
            {
                tagList.Add(id);
                if (info.Storage == StoragePath.Shared)
                    sharedComponentIds.Add(id);
            }
            else if (info.Storage == StoragePath.Table)
            {
                int columnIndex = tableList.Count;
                tableList.Add(id);

                if (info.IsEnableable)
                {
                    enableableComponentIds.Add(id);
                    enableableColumnIndices.Add(columnIndex);
                }

                if (info.IsCleanup)
                    cleanupComponentIds.Add(id);
            }
        }

        _tableComponentIds = tableList.ToArray();
        _tagIds = tagList.ToArray();
        _enableableComponentIds = enableableComponentIds.ToArray();
        _enableableColumnIndices = enableableColumnIndices.ToArray();
        _cleanupComponentIds = cleanupComponentIds.ToArray();
        HasCleanupComponents = _cleanupComponentIds.Length > 0;
        _sharedComponentIds = sharedComponentIds.ToArray();

        _columnOperations = new ComponentOperations[_tableComponentIds.Length];
        int totalComponentSize = 0;
        for (int i = 0; i < _tableComponentIds.Length; i++)
        {
            ref readonly ComponentInfo info = ref ComponentRegistry.Get(_tableComponentIds[i]);
            _columnOperations[i] = info.Operations;
            totalComponentSize += info.Size;
        }

        // Every table column owns two per-row uint version arrays in addition to its value array.
        // Fixed logical payload includes the chunk change version, enable mask and shared-value
        // entries. Managed object/array headers are runtime-specific and intentionally excluded.
        int rowVersionBytes = checked(_tableComponentIds.Length * 2 * sizeof(uint));
        ChunkRowPayloadBytes = checked(EntitySize + totalComponentSize + rowVersionBytes);
        ChunkFixedPayloadBytes = checked(
            (_tableComponentIds.Length * sizeof(uint)) +
            (_enableableComponentIds.Length * 16) +
            (_sharedComponentIds.Length * sizeof(int)));
        MaxChunkRows = ComputeChunkCapacity(
            ChunkRowPayloadBytes,
            ChunkFixedPayloadBytes,
            MaxChunkLogicalPayloadBytes);
        InitialChunkRows = Math.Min(
            MaxChunkRows,
            ComputeChunkCapacity(
                ChunkRowPayloadBytes,
                ChunkFixedPayloadBytes,
                StartChunkLogicalPayloadBytes));
        NextChunkRows = InitialChunkRows;
    }

    /// <summary>Builds a runtime-empty detached shell with independently owned shape arrays.</summary>
    internal Archetype(Archetype source)
    {
        ArgumentNullException.ThrowIfNull(source);

        PersistentIdentity = source.PersistentIdentity;
        ArchetypeId = source.ArchetypeId;
        _componentIds = CloneOwned(source.ComponentIds);
        _tableComponentIds = CloneOwned(source.TableComponentIds);
        _tagIds = CloneOwned(source.TagIds);
        TypeIdHash = source.TypeIdHash;
        _columnOperations = CloneOwned(source.ColumnOperations);
        _enableableComponentIds = CloneOwned(source.EnableableComponentIds);
        _enableableColumnIndices = CloneOwned(source.EnableableColumnIndices);
        MaxChunkRows = source.MaxChunkRows;
        InitialChunkRows = source.InitialChunkRows;
        NextChunkRows = source.NextChunkRows;
        ChunkRowPayloadBytes = source.ChunkRowPayloadBytes;
        ChunkFixedPayloadBytes = source.ChunkFixedPayloadBytes;
        _sharedComponentIds = CloneOwned(source.SharedComponentIds);
        HasCleanupComponents = source.HasCleanupComponents;
        _cleanupComponentIds = CloneOwned(source.CleanupComponentIds);
        FirstOpenChunk = source.FirstOpenChunk;
    }

    internal void CloneRuntimeStateTo(
        Archetype candidate,
        DetachedTableMap tableMap,
        bool cloneDerivedCaches)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(tableMap);

        if (cloneDerivedCaches)
        {
            foreach ((int componentId, StructuralTransition transition) in _addTransitions)
            {
                candidate._addTransitions.Add(
                    componentId,
                    CloneTransition(transition, tableMap));
            }

            foreach ((int componentId, StructuralTransition transition) in _removeTransitions)
            {
                candidate._removeTransitions.Add(
                    componentId,
                    CloneTransition(transition, tableMap));
            }

            foreach ((SortedValueKey key, StructuralTransition transition) in _includeTransitions)
            {
                candidate._includeTransitions.Add(
                    new SortedValueKey(key.Ids),
                    CloneTransition(transition, tableMap));
            }

            candidate.HasCleanupTransition = HasCleanupTransition;
            if (HasCleanupTransition)
            {
                candidate.CleanupTransition =
                    CloneTransition(CleanupTransition, tableMap);
            }
        }

        // Shared buckets are allocation indices. Clone their non-full chunk order while retaining
        // the immutable canonical tuple identity and independently owned mutable bucket state.
        foreach ((SharedComponentTuple key, SharedChunkBucket sourceBucket) in _sharedChunkBuckets)
        {
            var bucket = sourceBucket.CloneEmpty();
            foreach (Chunk sourceChunk in sourceBucket.OpenChunkSpan)
            {
                if (!ReferenceEquals(sourceChunk.SharedValues, key))
                {
                    throw new InvalidOperationException(
                        "Shared chunk bucket and chunk must use the same canonical tuple.");
                }

                Chunk candidateChunk = tableMap.Remap(sourceChunk);
                int candidateIndex = candidateChunk.IndexInArchetype;
                if ((uint)candidateIndex >= (uint)candidate._chunks.Count ||
                    !ReferenceEquals(candidate._chunks[candidateIndex], candidateChunk))
                {
                    throw new InvalidOperationException(
                        "Shared chunk bucket contains a chunk from another archetype.");
                }

                if (!ReferenceEquals(candidateChunk.SharedValues, key))
                {
                    throw new InvalidOperationException(
                        "Detached shared chunks must retain the immutable canonical tuple.");
                }

                bucket.AddClonedOpenChunk(candidateChunk);
            }

            candidate._sharedChunkBuckets.Add(key, bucket);
        }
    }

    private static StructuralTransition CloneTransition(
        StructuralTransition source,
        DetachedTableMap tableMap) =>
        new(
            tableMap.Remap(source.Target),
            CloneOwned(source.SharedColumns));

    private int ComputeChunkCapacity(int rowSize, int fixedSize, int chunkSizeBytes)
    {
        int rowBudget = Math.Max(0, chunkSizeBytes - fixedSize);
        int capacity = rowSize > 0 ? Math.Max(1, rowBudget / rowSize) : 128;
        if (_enableableComponentIds.Length > 0)
            capacity = Math.Min(capacity, 128);

        return capacity;
    }

    public bool HasComponent(int componentId) =>
        _componentIds.AsSpan().BinarySearch(componentId) >= 0;

    public int Column(int componentId)
    {
        int index = _tableComponentIds.AsSpan().BinarySearch(componentId);
        if (index < 0)
            throw new KeyNotFoundException(
                $"Component ID {componentId} is not a table component of Archetype {ArchetypeId}.");
        return index;
    }

    public bool TryColumn(int componentId, out int columnIndex)
    {
        int index = _tableComponentIds.AsSpan().BinarySearch(componentId);
        if (index >= 0)
        {
            columnIndex = index;
            return true;
        }

        columnIndex = -1;
        return false;
    }

    public bool TryMask(int componentId, out int maskIndex)
    {
        int index = _enableableComponentIds.AsSpan().BinarySearch(componentId);
        if (index >= 0)
        {
            maskIndex = index;
            return true;
        }

        maskIndex = -1;
        return false;
    }

    public int EnableMask(int componentId)
    {
        if (!TryMask(componentId, out int maskIndex))
            throw new KeyNotFoundException(
                $"Component ID {componentId} is not an enableable component of Archetype {ArchetypeId}.");

        return maskIndex;
    }

    private static T[] CloneOwned<T>(ReadOnlySpan<T> source)
    {
        var candidate = new T[source.Length];
        source.CopyTo(candidate);
        return candidate;
    }
}

