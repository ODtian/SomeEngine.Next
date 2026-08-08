using System.Buffers;
using System.Runtime.InteropServices;
using SomeEngine.ECS.Collections;
using SomeEngine.ECS.Registry;

namespace SomeEngine.ECS.Archetypes;

/// <summary>
/// 全局 Archetype 注册表。基于 sorted component id equality。
/// 负责 archetypeId 分配、边缓存计算、SharedColumnMapping 预计算。
/// </summary>
/// <remarks>
/// 设计引用：docs/DESIGN.md §5.1, §5.2
/// </remarks>
internal class ArchetypeRegistry
{
    private const int MaximumStackComponentIds = 256;
    private readonly Dictionary<SortedValueKey, Archetype> _archetypesByComponents =
        new(SortedValueComparer.Instance);
    private readonly List<Archetype> _allArchetypes = new();
    private int _nextArchetypeId;

    /// <summary>新 Archetype 创建时触发的回调。</summary>
    internal Action<Archetype>? OnArchetypeCreated;

    /// <summary>所有已创建的 Archetype 只读视图。</summary>
    public ReadOnlySpan<Archetype> AllArchetypes => CollectionsMarshal.AsSpan(_allArchetypes);

    internal int Count => _allArchetypes.Count;

    internal Archetype At(int index) => _allArchetypes[index];

    /// <summary>
    /// Clones the complete table image without running archetype-created callbacks. Archetypes are
    /// emitted in the original list/id order and every transition target is remapped only after all
    /// candidate shells and chunks exist.
    /// </summary>
    internal ArchetypeRegistry CloneExact(out DetachedTableMap tableMap) =>
        CloneExact(out tableMap, cloneDerivedCaches: true);

    internal ArchetypeRegistry CloneExact(
        out DetachedTableMap tableMap,
        bool cloneDerivedCaches)
    {
        var candidate = new ArchetypeRegistry
        {
            _nextArchetypeId = _nextArchetypeId,
        };
        tableMap = new DetachedTableMap();

        candidate._allArchetypes.Capacity = _allArchetypes.Capacity;
        foreach (var sourceArchetype in _allArchetypes)
        {
            var candidateArchetype = new Archetype(sourceArchetype);

            candidate._allArchetypes.Add(candidateArchetype);
            candidate._archetypesByComponents.Add(
                new SortedValueKey(candidateArchetype.ComponentIds),
                candidateArchetype);
            tableMap.Add(sourceArchetype, candidateArchetype);
        }

        foreach (var sourceArchetype in _allArchetypes)
        {
            var candidateArchetype = tableMap.Remap(sourceArchetype);
            candidateArchetype.EnsureChunkListCapacity(sourceArchetype.ChunkListCapacity);
            foreach (var sourceChunk in sourceArchetype.Chunks)
            {
                var candidateChunk =
                    sourceChunk.ForkDetached(candidateArchetype.ColumnOperations);
                candidateArchetype.AddChunk(candidateChunk);
                tableMap.Add(sourceChunk, candidateChunk);
            }
        }

        foreach (var sourceArchetype in _allArchetypes)
        {
            sourceArchetype.CloneRuntimeStateTo(
                tableMap.Remap(sourceArchetype),
                tableMap,
                cloneDerivedCaches);
        }

        // OnArchetypeCreated intentionally remains null. The owner which installs the candidate
        // image is responsible for binding its own callback after the clone is complete.
        return candidate;
    }

    /// <summary>
    /// 查找或创建 Archetype。
    /// </summary>
    /// <param name="sortedIds">已排序的组件 ID span。</param>
    public Archetype GetOrCreate(ReadOnlySpan<int> sortedIds)
    {
        var lookup = _archetypesByComponents.GetAlternateLookup<ReadOnlySpan<int>>();
        if (lookup.TryGetValue(sortedIds, out var existing))
            return existing;

        var created = new Archetype(_nextArchetypeId++, sortedIds);
        _allArchetypes.Add(created);
        _archetypesByComponents.Add(new SortedValueKey(created.ComponentIds), created);
        OnArchetypeCreated?.Invoke(created);
        return created;
    }

    /// <summary>
    /// 获取或创建 Add 边缓存：source + componentId → target archetype。
    /// </summary>
    public StructuralTransition AddEdge(Archetype source, int componentId)
    {
        if (source.TryGetAddTransition(componentId, out var existing))
            return existing;

        var newIds = InsertSorted(source.ComponentIds, componentId);
        var target = GetOrCreate(newIds);

        var mapping = SharedMap(source, target);
        var edge = new StructuralTransition(target, mapping);
        source.CacheAddTransition(componentId, edge);

        if (!target.TryGetRemoveTransition(componentId, out _))
        {
            var reverseMapping = SharedMap(target, source);
            target.CacheRemoveTransition(
                componentId,
                new StructuralTransition(source, reverseMapping));
        }

        return edge;
    }

    public StructuralTransition IncludeTransition(Archetype source, ReadOnlySpan<int> componentIds)
    {
        if (componentIds.Length == 0)
            return new StructuralTransition(source, Array.Empty<SharedColumnMapping>());

        int[]? rentedMissing = null;
        Span<int> missingComponentIds = componentIds.Length <= MaximumStackComponentIds
            ? stackalloc int[componentIds.Length]
            : (rentedMissing = ArrayPool<int>.Shared.Rent(componentIds.Length))
                .AsSpan(0, componentIds.Length);
        try
        {
            int missingCount = 0;
            for (int i = 0; i < componentIds.Length; i++)
            {
                int componentId = componentIds[i];
                if (!source.HasComponent(componentId))
                    missingComponentIds[missingCount++] = componentId;
            }

            if (missingCount == 0)
                return new StructuralTransition(source, Array.Empty<SharedColumnMapping>());

            if (missingCount == 1)
                return AddEdge(source, missingComponentIds[0]);

            ReadOnlySpan<int> missingSpan = missingComponentIds[..missingCount];
            if (source.TryGetIncludeTransition(missingSpan, out var cachedPlan))
                return cachedPlan;

            int finalCount = checked(source.ComponentIds.Length + missingCount);
            int[]? rentedFinal = null;
            Span<int> finalIds = finalCount <= MaximumStackComponentIds
                ? stackalloc int[finalCount]
                : (rentedFinal = ArrayPool<int>.Shared.Rent(finalCount)).AsSpan(0, finalCount);
            try
            {
                MergeSorted(source.ComponentIds, missingSpan, finalIds);
                var target = GetOrCreate(finalIds);
                var mapping = SharedMap(source, target);
                var plan = new StructuralTransition(target, mapping);

                source.CacheIncludeTransition(missingSpan, plan);
                return plan;
            }
            finally
            {
                if (rentedFinal is not null)
                    ArrayPool<int>.Shared.Return(rentedFinal);
            }
        }
        finally
        {
            if (rentedMissing is not null)
                ArrayPool<int>.Shared.Return(rentedMissing);
        }
    }

    public StructuralTransition CleanupTransition(Archetype source)
    {
        if (!source.HasCleanupComponents || source.CleanupComponentIds.Length == source.ComponentIds.Length)
            return new StructuralTransition(source, Array.Empty<SharedColumnMapping>());

        if (source.HasCleanupTransition)
            return source.CleanupTransition;

        var target = GetOrCreate(source.CleanupComponentIds);
        var mapping = SharedMap(source, target);
        var plan = new StructuralTransition(target, mapping);
        source.CleanupTransition = plan;
        source.HasCleanupTransition = true;
        return plan;
    }

    /// <summary>
    /// 获取或创建 Remove 边缓存：source - componentId → target archetype。
    /// </summary>
    public StructuralTransition RemoveEdge(Archetype source, int componentId)
    {
        if (source.TryGetRemoveTransition(componentId, out var existing))
            return existing;

        var newIds = RemoveSorted(source.ComponentIds, componentId);
        var target = GetOrCreate(newIds);

        var mapping = SharedMap(source, target);
        var edge = new StructuralTransition(target, mapping);
        source.CacheRemoveTransition(componentId, edge);

        if (!target.TryGetAddTransition(componentId, out _))
        {
            var reverseMapping = SharedMap(target, source);
            target.CacheAddTransition(
                componentId,
                new StructuralTransition(source, reverseMapping));
        }

        return edge;
    }

    /// <summary>
    /// 计算 source → destination 的共享列映射。
    /// </summary>
    internal static SharedColumnMapping[] SharedMap(Archetype source, Archetype destination)
    {
        int sharedCount = 0;
        for (int sourceColumn = 0; sourceColumn < source.TableComponentIds.Length; sourceColumn++)
        {
            int componentId = source.TableComponentIds[sourceColumn];
            if (destination.TryColumn(componentId, out int destinationColumn))
                sharedCount++;
        }

        if (sharedCount == 0)
            return Array.Empty<SharedColumnMapping>();

        var result = new SharedColumnMapping[sharedCount];
        int resultIndex = 0;
        for (int sourceColumn = 0; sourceColumn < source.TableComponentIds.Length; sourceColumn++)
        {
            int componentId = source.TableComponentIds[sourceColumn];
            if (destination.TryColumn(componentId, out int destinationColumn))
            {
                ref readonly ComponentOperations operations =
                    ref source.ColumnOperations[sourceColumn];
                result[resultIndex++] = new SharedColumnMapping(
                    sourceColumn, destinationColumn, operations);
            }
        }

        return result;
    }

    /// <summary>在有序数组中插入一个值，返回新数组。</summary>
    internal static int[] InsertSorted(ReadOnlySpan<int> sorted, int value)
    {
        var result = new int[sorted.Length + 1];
        int insertIndex = sorted.BinarySearch(value);
        if (insertIndex < 0) insertIndex = ~insertIndex;
        sorted[..insertIndex].CopyTo(result);
        result[insertIndex] = value;
        sorted[insertIndex..].CopyTo(result.AsSpan(insertIndex + 1));
        return result;
    }

    /// <summary>从有序数组中移除一个值，返回新数组。</summary>
    internal static int[] RemoveSorted(ReadOnlySpan<int> sorted, int value)
    {
        int index = sorted.BinarySearch(value);
        if (index < 0)
            throw new InvalidOperationException($"Cannot remove component ID {value}: not found in archetype.");
        var result = new int[sorted.Length - 1];
        sorted[..index].CopyTo(result);
        sorted[(index + 1)..].CopyTo(result.AsSpan(index));
        return result;
    }

    private static void MergeSorted(ReadOnlySpan<int> source, ReadOnlySpan<int> additions, Span<int> destination)
    {
        int sourceIndex = 0;
        int additionIndex = 0;
        int destinationIndex = 0;

        while (sourceIndex < source.Length && additionIndex < additions.Length)
        {
            if (source[sourceIndex] < additions[additionIndex])
                destination[destinationIndex++] = source[sourceIndex++];
            else
                destination[destinationIndex++] = additions[additionIndex++];
        }

        while (sourceIndex < source.Length)
            destination[destinationIndex++] = source[sourceIndex++];

        while (additionIndex < additions.Length)
            destination[destinationIndex++] = additions[additionIndex++];
    }

}

