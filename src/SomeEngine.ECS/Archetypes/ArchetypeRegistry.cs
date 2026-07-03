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
    private readonly Dictionary<SortedValueKey, Archetype> _archetypesByComponents =
        new(SortedValueComparer.Instance);
    private readonly List<Archetype> _allArchetypes = new();
    private int _nextArchetypeId;

    /// <summary>新 Archetype 创建时触发的回调。</summary>
    internal Action<Archetype>? OnArchetypeCreated;

    /// <summary>所有已创建的 Archetype 只读视图。</summary>
    public IReadOnlyList<Archetype> AllArchetypes => _allArchetypes;

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
    public ArchetypeEdge AddEdge(Archetype source, int componentId)
    {
        if (source.AddEdges.TryGetValue(componentId, out var existing))
            return existing;

        var newIds = InsertSorted(source.ComponentIds, componentId);
        var target = GetOrCreate(newIds);

        var mapping = SharedMap(source, target);
        var edge = new ArchetypeEdge(target, mapping);
        source.AddEdges[componentId] = edge;

        if (!target.RemoveEdges.ContainsKey(componentId))
        {
            var reverseMapping = SharedMap(target, source);
            target.RemoveEdges[componentId] = new ArchetypeEdge(source, reverseMapping);
        }

        return edge;
    }

    public StructuralTransition IncludeTransition(Archetype source, ReadOnlySpan<int> componentIds)
    {
        if (componentIds.Length == 0)
            return new StructuralTransition(source, Array.Empty<SharedColumnMapping>());

        Span<int> missingComponentIds = stackalloc int[componentIds.Length];
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
            return AddEdge(source, missingComponentIds[0]).AsTransition();

        var missingSpan = missingComponentIds[..missingCount];
        var cacheLookup = source.IncludeTransitionCache.GetAlternateLookup<ReadOnlySpan<int>>();
        if (cacheLookup.TryGetValue(missingSpan, out var cachedPlan))
            return cachedPlan;

        Span<int> finalIds = stackalloc int[source.ComponentIds.Length + missingCount];
        MergeSorted(source.ComponentIds, missingSpan, finalIds);
        var target = GetOrCreate(finalIds[..(source.ComponentIds.Length + missingCount)]);
        var mapping = SharedMap(source, target);
        var plan = new StructuralTransition(target, mapping);

        source.IncludeTransitionCache.Add(SortedValueKey.CreateOwnedCopy(missingSpan), plan);
        return plan;
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
    public ArchetypeEdge RemoveEdge(Archetype source, int componentId)
    {
        if (source.RemoveEdges.TryGetValue(componentId, out var existing))
            return existing;

        var newIds = RemoveSorted(source.ComponentIds, componentId);
        var target = GetOrCreate(newIds);

        var mapping = SharedMap(source, target);
        var edge = new ArchetypeEdge(target, mapping);
        source.RemoveEdges[componentId] = edge;

        if (!target.AddEdges.ContainsKey(componentId))
        {
            var reverseMapping = SharedMap(target, source);
            target.AddEdges[componentId] = new ArchetypeEdge(source, reverseMapping);
        }

        return edge;
    }

    /// <summary>
    /// 计算 source → destination 的共享列映射。
    /// </summary>
    internal static SharedColumnMapping[] SharedMap(Archetype source, Archetype destination)
    {
        int sharedCount = 0;
        for (int sourceColumn = 0; sourceColumn < source.ColumnMetas.Length; sourceColumn++)
        {
            int componentId = source.ColumnMetas[sourceColumn].ComponentId;
            if (destination.TryColumn(componentId, out int destinationColumn))
                sharedCount++;
        }

        if (sharedCount == 0)
            return Array.Empty<SharedColumnMapping>();

        var result = new SharedColumnMapping[sharedCount];
        int resultIndex = 0;
        for (int sourceColumn = 0; sourceColumn < source.ColumnMetas.Length; sourceColumn++)
        {
            int componentId = source.ColumnMetas[sourceColumn].ComponentId;
            if (destination.TryColumn(componentId, out int destinationColumn))
            {
                ref readonly var meta = ref source.ColumnMetas[sourceColumn];
                result[resultIndex++] = new SharedColumnMapping(
                    sourceColumn, destinationColumn, meta.Operations);
            }
        }

        return result;
    }

    /// <summary>在有序数组中插入一个值，返回新数组。</summary>
    internal static int[] InsertSorted(int[] sorted, int value)
    {
        var result = new int[sorted.Length + 1];
        int insertIndex = Array.BinarySearch(sorted, value);
        if (insertIndex < 0) insertIndex = ~insertIndex;
        Array.Copy(sorted, 0, result, 0, insertIndex);
        result[insertIndex] = value;
        Array.Copy(sorted, insertIndex, result, insertIndex + 1, sorted.Length - insertIndex);
        return result;
    }

    /// <summary>从有序数组中移除一个值，返回新数组。</summary>
    internal static int[] RemoveSorted(int[] sorted, int value)
    {
        int index = Array.BinarySearch(sorted, value);
        if (index < 0)
            throw new InvalidOperationException($"Cannot remove component ID {value}: not found in archetype.");
        var result = new int[sorted.Length - 1];
        Array.Copy(sorted, 0, result, 0, index);
        Array.Copy(sorted, index + 1, result, index, sorted.Length - index - 1);
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

