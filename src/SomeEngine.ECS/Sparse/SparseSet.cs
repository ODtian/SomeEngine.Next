using SomeEngine.ECS.Collections;
using SomeEngine.ECS.Entities;

namespace SomeEngine.ECS.Sparse;

internal interface ISparseSet
{
    bool Has(Entity entity);

    void AddCopy(Entity source, Entity target);

    void ReplaceCopy(Entity source, Entity target);

    bool RemoveOptional(Entity entity);
}

/// <summary>
/// 分页 sparse 数组 + dense 数组的侧存储容器。
/// 不参与 Archetype identity，Add/Remove 不触发 archetype 迁移。
/// </summary>
/// <remarks>
/// 设计引用：docs/DESIGN.md §6.1
/// - 分页 sparse 避免百万 entity 时内存膨胀
/// - dense 数组紧凑排列，cache-friendly 迭代
/// </remarks>
public sealed class SparseSet<T> : ISparseSet where T : struct
{
    private const int PageSize = 4096;
    private const int PageShift = 12;
    private const int PageMask = PageSize - 1;
    private const int SentinelValue = -1;

    private int[][] _sparsePages;
    private Entity[] _denseEntities;
    private T[] _denseData;
    private int _count;

    public SparseSet(int initialDenseCapacity = 16)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(initialDenseCapacity);

        _sparsePages = new int[4][];
        _denseEntities = new Entity[initialDenseCapacity];
        _denseData = new T[initialDenseCapacity];
        _count = 0;
    }

    /// <summary>当前元素数。</summary>
    public int Count => _count;

    /// <summary>检查 entity 是否存在。</summary>
    public bool Has(Entity entity)
    {
        return TryDenseIndex(entity, out _, out _);
    }

    /// <summary>添加组件。重复添加抛异常。</summary>
    public void Add(Entity entity, in T value)
    {
        ThrowInvalidEntity(entity);

        if (Has(entity))
            throw new InvalidOperationException($"Entity {entity} already has this sparse component.");

        EnsureDenseCapacity(_count + 1);
        int page = entity.Index >> PageShift;
        EnsurePage(page);

        int denseIndex = _count;
        _denseEntities[denseIndex] = entity;
        _denseData[denseIndex] = value;
        _sparsePages[page][entity.Index & PageMask] = denseIndex;
        _count++;
    }

    /// <summary>替换组件。不存在时抛异常。</summary>
    public void Replace(Entity entity, in T value)
    {
        ThrowInvalidEntity(entity);

        if (!TryDenseIndex(entity, out _, out int denseIndex))
            throw new InvalidOperationException($"Entity {entity} does not have this sparse component.");

        _denseData[denseIndex] = value;
    }

    /// <summary>移除组件。不存在时抛异常。</summary>
    public void Remove(Entity entity)
    {
        ThrowInvalidEntity(entity);

        if (!TryDenseIndex(entity, out int page, out int denseIndex))
            throw new InvalidOperationException($"Entity {entity} does not have this sparse component.");

        RemoveDense(entity, page, denseIndex);
    }

    bool ISparseSet.RemoveOptional(Entity entity)
    {
        ThrowInvalidEntity(entity);

        if (!TryDenseIndex(entity, out int page, out int denseIndex))
            return false;

        RemoveDense(entity, page, denseIndex);
        return true;
    }

    void ISparseSet.AddCopy(Entity source, Entity target)
    {
        var value = Read(source);
        Add(target, in value);
    }

    void ISparseSet.ReplaceCopy(Entity source, Entity target)
    {
        var value = Read(source);
        Replace(target, in value);
    }

    private void RemoveDense(Entity entity, int page, int denseIndex)
    {
        int lastDenseIndex = _count - 1;

        if (denseIndex != lastDenseIndex)
        {
            // swap-remove: 把最后一个元素移到被删位置
            _denseEntities[denseIndex] = _denseEntities[lastDenseIndex];
            _denseData[denseIndex] = _denseData[lastDenseIndex];

            // 更新被交换 entity 的 sparse 映射
            var movedEntity = _denseEntities[denseIndex];
            int movedPage = movedEntity.Index >> PageShift;
            _sparsePages[movedPage][movedEntity.Index & PageMask] = denseIndex;
        }

        // 清除旧映射和末尾数据
        _sparsePages[page][entity.Index & PageMask] = SentinelValue;
        _denseEntities[lastDenseIndex] = default;
        _denseData[lastDenseIndex] = default;
        _count--;
    }

    /// <summary>获取组件的 ref 引用。</summary>
    public ref T Get(Entity entity)
    {
        ThrowInvalidEntity(entity);

        if (!TryDenseIndex(entity, out _, out int denseIndex))
            throw new InvalidOperationException($"Entity {entity} does not have this sparse component.");

        return ref _denseData[denseIndex];
    }

    /// <summary>读取组件值（返回拷贝）。</summary>
    public T Read(Entity entity)
    {
        return Get(entity);
    }

    /// <summary>紧凑排列的 entity 列表。</summary>
    public ReadOnlySpan<Entity> DenseEntities => _denseEntities.AsSpan(0, _count);

    /// <summary>紧凑排列的组件数据。</summary>
    public ReadOnlySpan<T> DenseData => _denseData.AsSpan(0, _count);

    private void EnsureDenseCapacity(int required)
    {
        ArrayGrowthExtensions.EnsureCapacity(ref _denseEntities, required, 16);
        ArrayGrowthExtensions.EnsureCapacity(ref _denseData, required, 16);
    }

    private void EnsurePage(int pageIndex)
    {
        ArrayGrowthExtensions.EnsureCapacity(ref _sparsePages, pageIndex + 1, 4);

        if (_sparsePages[pageIndex] == null)
        {
            _sparsePages[pageIndex] = new int[PageSize];
            Array.Fill(_sparsePages[pageIndex], SentinelValue);
        }
    }

    private bool TryDenseIndex(Entity entity, out int page, out int denseIndex)
    {
        page = 0;
        denseIndex = SentinelValue;

        if (entity.Index <= 0)
            return false;

        page = entity.Index >> PageShift;
        if (page >= _sparsePages.Length || _sparsePages[page] == null)
            return false;

        denseIndex = _sparsePages[page][entity.Index & PageMask];
        return denseIndex >= 0 && denseIndex < _count && _denseEntities[denseIndex] == entity;
    }

    private static void ThrowInvalidEntity(Entity entity)
    {
        if (entity.Index <= 0)
            throw new InvalidOperationException($"Entity {entity} is not valid for sparse storage.");
    }
}

