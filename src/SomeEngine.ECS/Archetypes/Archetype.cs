using SomeEngine.ECS.Collections;
using SomeEngine.ECS.Registry;

namespace SomeEngine.ECS.Archetypes;

public class Archetype
{
    public int ArchetypeId { get; }
    public int[] ComponentIds { get; }
    public int[] TableComponentIds { get; }
    public int[] TagIds { get; }
    public uint TypeIdHash { get; }
    public ColumnMetadata[] ColumnMetas { get; }
    public int[] EnableableComponentIds { get; }
    public int[] EnableableColumnIndices { get; }
    public int MaxChunkRows { get; }
    internal int InitialChunkRows { get; }
    internal int NextChunkRows { get; set; }

    /// <summary>SharedComponent 类型的 componentId 子集（从 TagIds 中筛选）。</summary>
    public int[] SharedComponentIds { get; }
    internal Dictionary<SortedValueKey, List<Chunk>> SharedChunkBuckets { get; } =
        new(SortedValueComparer.Instance);



    internal List<Chunk> Chunks { get; } = new();

    /// <summary>
    /// O(1) 分配提示：第一个可能非满 chunk 的索引。
    /// Maintained by allocation and chunk recycling paths.
    /// </summary>
    internal int FirstOpenChunk { get; set; }
    internal Dictionary<int, ArchetypeEdge> AddEdges { get; } = new();
    internal Dictionary<int, ArchetypeEdge> RemoveEdges { get; } = new();
    internal Dictionary<SortedValueKey, StructuralTransition> IncludeTransitionCache { get; } =
        new(SortedValueComparer.Instance);
    internal bool HasCleanupTransition { get; set; }
    internal StructuralTransition CleanupTransition { get; set; }
    internal bool HasCleanupComponents { get; }
    internal int[] CleanupComponentIds { get; }

    private const int StartChunkBytes = 8192;
    private const int MaxChunkBytes = 2097152;
    private const int EntitySize = 8;

    internal Archetype(int archetypeId, ReadOnlySpan<int> sortedComponentIds)
    {
        ArchetypeId = archetypeId;
        ComponentIds = sortedComponentIds.ToArray();
        TypeIdHash = StableHash.Compute(sortedComponentIds);

        var tableList = new List<int>();
        var tagList = new List<int>();
        var enableableComponentIds = new List<int>();
        var enableableColumnIndices = new List<int>();
        var cleanupComponentIds = new List<int>();
        var sharedComponentIds = new List<int>();

        foreach (int id in sortedComponentIds)
        {
            ref var info = ref ComponentRegistry.Get(id);
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

        TableComponentIds = tableList.ToArray();
        TagIds = tagList.ToArray();
        EnableableComponentIds = enableableComponentIds.ToArray();
        EnableableColumnIndices = enableableColumnIndices.ToArray();
        CleanupComponentIds = cleanupComponentIds.ToArray();
        HasCleanupComponents = CleanupComponentIds.Length > 0;
        SharedComponentIds = sharedComponentIds.ToArray();

        ColumnMetas = new ColumnMetadata[TableComponentIds.Length];
        int totalComponentSize = 0;
        for (int i = 0; i < TableComponentIds.Length; i++)
        {
            ref var info = ref ComponentRegistry.Get(TableComponentIds[i]);
            ColumnMetas[i] = new ColumnMetadata(info.Id, info.Operations);
            totalComponentSize += info.Size;
        }

        int rowSize = EntitySize + totalComponentSize;
        MaxChunkRows = ComputeChunkCapacity(rowSize, MaxChunkBytes);
        InitialChunkRows = Math.Min(
            MaxChunkRows,
            ComputeChunkCapacity(rowSize, StartChunkBytes));
        NextChunkRows = InitialChunkRows;
    }

    private int ComputeChunkCapacity(int rowSize, int chunkSizeBytes)
    {
        int capacity = rowSize > 0 ? Math.Max(1, chunkSizeBytes / rowSize) : 128;
        if (EnableableComponentIds.Length > 0)
            capacity = Math.Min(capacity, 128);

        return capacity;
    }

    public bool HasComponent(int componentId) =>
        Array.BinarySearch(ComponentIds, componentId) >= 0;

    public int Column(int componentId)
    {
        int index = Array.BinarySearch(TableComponentIds, componentId);
        if (index < 0)
            throw new KeyNotFoundException(
                $"Component ID {componentId} is not a table component of Archetype {ArchetypeId}.");
        return index;
    }

    public bool TryColumn(int componentId, out int columnIndex)
    {
        int index = Array.BinarySearch(TableComponentIds, componentId);
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
        int index = Array.BinarySearch(EnableableComponentIds, componentId);
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
}

