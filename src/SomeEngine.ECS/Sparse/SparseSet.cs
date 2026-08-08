using SomeEngine.ECS.Collections;
using SomeEngine.ECS.Entities;

namespace SomeEngine.ECS.Sparse;

internal interface ISparseSet
{
    bool Has(Entity entity);

    void AddCopy(Entity source, Entity target);

    void ReplaceCopy(Entity source, Entity target);

    bool RemoveOptional(Entity entity);

    ISparseSet CloneDetached();
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

    private Storage _storage;
    private int _detachCount;

    public SparseSet(int initialDenseCapacity = 16)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(initialDenseCapacity);

        _storage = new Storage(
            new int[4][],
            new Entity[initialDenseCapacity],
            new T[initialDenseCapacity],
            count: 0);
    }

    private SparseSet(Storage storage)
    {
        _storage = storage;
    }

    /// <summary>当前元素数。</summary>
    public int Count => _storage.Count;

    /// <summary>
    /// Identifies the immutable-or-exclusively-owned storage generation. Detached transaction
    /// candidates initially retain this identity and replace it only before their first write.
    /// </summary>
    internal object BackingIdentity => _storage;

    /// <summary>Number of shared generations this wrapper detached before exposing a write.</summary>
    internal int DetachCount => _detachCount;

    /// <summary>检查 entity 是否存在。</summary>
    public bool Has(Entity entity)
    {
        return TryDenseIndex(_storage, entity, out _, out _);
    }

    /// <summary>添加组件。重复添加抛异常。</summary>
    public void Add(Entity entity, in T value)
    {
        ThrowInvalidEntity(entity);

        Storage storage = _storage;
        if (TryDenseIndex(storage, entity, out _, out _))
            throw new InvalidOperationException($"Entity {entity} already has this sparse component.");

        storage = WritableStorage();
        EnsureDenseCapacity(storage, storage.Count + 1);
        int page = entity.Index >> PageShift;
        EnsurePage(storage, page);

        int denseIndex = storage.Count;
        storage.DenseEntities[denseIndex] = entity;
        storage.DenseData[denseIndex] = value;
        storage.SparsePages[page][entity.Index & PageMask] = denseIndex;
        storage.Count++;
    }

    /// <summary>替换组件。不存在时抛异常。</summary>
    public void Replace(Entity entity, in T value)
    {
        ThrowInvalidEntity(entity);

        Storage storage = _storage;
        if (!TryDenseIndex(storage, entity, out _, out int denseIndex))
            throw new InvalidOperationException($"Entity {entity} does not have this sparse component.");

        storage = WritableStorage();
        storage.DenseData[denseIndex] = value;
    }

    /// <summary>移除组件。不存在时抛异常。</summary>
    public void Remove(Entity entity)
    {
        ThrowInvalidEntity(entity);

        Storage storage = _storage;
        if (!TryDenseIndex(storage, entity, out int page, out int denseIndex))
            throw new InvalidOperationException($"Entity {entity} does not have this sparse component.");

        RemoveDense(WritableStorage(), entity, page, denseIndex);
    }

    bool ISparseSet.RemoveOptional(Entity entity)
    {
        ThrowInvalidEntity(entity);

        Storage storage = _storage;
        if (!TryDenseIndex(storage, entity, out int page, out int denseIndex))
            return false;

        RemoveDense(WritableStorage(), entity, page, denseIndex);
        return true;
    }

    ISparseSet ISparseSet.CloneDetached() => CloneDetached();

    /// <summary>
    /// Creates an exact logical image that shares its read-only generation until either wrapper
    /// requests mutable storage. The first write then copies dense storage and sparse pages once.
    /// </summary>
    internal SparseSet<T> CloneDetached()
    {
        Storage storage = _storage;
        storage.MarkShared();
        return new SparseSet<T>(storage);
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

    private static void RemoveDense(
        Storage storage,
        Entity entity,
        int page,
        int denseIndex)
    {
        int lastDenseIndex = storage.Count - 1;

        if (denseIndex != lastDenseIndex)
        {
            // swap-remove: 把最后一个元素移到被删位置
            storage.DenseEntities[denseIndex] = storage.DenseEntities[lastDenseIndex];
            storage.DenseData[denseIndex] = storage.DenseData[lastDenseIndex];

            // 更新被交换 entity 的 sparse 映射
            var movedEntity = storage.DenseEntities[denseIndex];
            int movedPage = movedEntity.Index >> PageShift;
            storage.SparsePages[movedPage][movedEntity.Index & PageMask] = denseIndex;
        }

        // 清除旧映射和末尾数据
        storage.SparsePages[page][entity.Index & PageMask] = SentinelValue;
        storage.DenseEntities[lastDenseIndex] = default;
        storage.DenseData[lastDenseIndex] = default;
        storage.Count--;
    }

    /// <summary>获取组件的 ref 引用。</summary>
    public ref T Get(Entity entity)
    {
        ThrowInvalidEntity(entity);

        Storage storage = _storage;
        if (!TryDenseIndex(storage, entity, out _, out int denseIndex))
            throw new InvalidOperationException($"Entity {entity} does not have this sparse component.");

        storage = WritableStorage();
        return ref storage.DenseData[denseIndex];
    }

    /// <summary>读取组件值（返回拷贝）。</summary>
    public T Read(Entity entity)
    {
        ThrowInvalidEntity(entity);

        Storage storage = _storage;
        if (!TryDenseIndex(storage, entity, out _, out int denseIndex))
            throw new InvalidOperationException($"Entity {entity} does not have this sparse component.");

        return storage.DenseData[denseIndex];
    }

    internal ref readonly T ReadRef(Entity entity)
    {
        ThrowInvalidEntity(entity);

        Storage storage = _storage;
        if (!TryDenseIndex(storage, entity, out _, out int denseIndex))
            throw new InvalidOperationException($"Entity {entity} does not have this sparse component.");

        return ref storage.DenseData[denseIndex];
    }

    /// <summary>紧凑排列的 entity 列表。</summary>
    public ReadOnlySpan<Entity> DenseEntities =>
        _storage.DenseEntities.AsSpan(0, _storage.Count);

    /// <summary>紧凑排列的组件数据。</summary>
    public ReadOnlySpan<T> DenseData => _storage.DenseData.AsSpan(0, _storage.Count);

    /// <summary>
    /// Returns the dense writable storage to the World owner. Public World callers receive this
    /// span only through a runtime-scoped callback, so it cannot outlive structural protection.
    /// </summary>
    internal Span<T> BorrowDenseWrite()
    {
        Storage storage = WritableStorage();
        return storage.DenseData.AsSpan(0, storage.Count);
    }

    private static void EnsureDenseCapacity(Storage storage, int required)
    {
        ArrayGrowthExtensions.EnsureCapacity(ref storage.DenseEntities, required, 16);
        ArrayGrowthExtensions.EnsureCapacity(ref storage.DenseData, required, 16);
    }

    private static void EnsurePage(Storage storage, int pageIndex)
    {
        ArrayGrowthExtensions.EnsureCapacity(ref storage.SparsePages, pageIndex + 1, 4);

        if (storage.SparsePages[pageIndex] == null)
        {
            storage.SparsePages[pageIndex] = new int[PageSize];
            Array.Fill(storage.SparsePages[pageIndex], SentinelValue);
        }
    }

    private static bool TryDenseIndex(
        Storage storage,
        Entity entity,
        out int page,
        out int denseIndex)
    {
        page = 0;
        denseIndex = SentinelValue;

        if (entity.Index <= 0)
            return false;

        page = entity.Index >> PageShift;
        if (page >= storage.SparsePages.Length || storage.SparsePages[page] == null)
            return false;

        denseIndex = storage.SparsePages[page][entity.Index & PageMask];
        return denseIndex >= 0 &&
            denseIndex < storage.Count &&
            storage.DenseEntities[denseIndex] == entity;
    }

    private Storage WritableStorage()
    {
        Storage storage = _storage;
        if (!storage.IsShared)
            return storage;

        storage = storage.CloneWritable();
        _storage = storage;
        _detachCount++;
        return storage;
    }

    private static void ThrowInvalidEntity(Entity entity)
    {
        if (entity.Index <= 0)
            throw new InvalidOperationException($"Entity {entity} is not valid for sparse storage.");
    }

    private sealed class Storage
    {
        private int _shared;

        internal Storage(
            int[][] sparsePages,
            Entity[] denseEntities,
            T[] denseData,
            int count)
        {
            SparsePages = sparsePages;
            DenseEntities = denseEntities;
            DenseData = denseData;
            Count = count;
        }

        internal int[][] SparsePages;

        internal Entity[] DenseEntities;

        internal T[] DenseData;

        internal int Count;

        internal bool IsShared => Volatile.Read(ref _shared) != 0;

        internal void MarkShared() => Volatile.Write(ref _shared, 1);

        internal Storage CloneWritable()
        {
            var sparsePages = new int[SparsePages.Length][];
            for (int i = 0; i < SparsePages.Length; i++)
            {
                int[]? page = SparsePages[i];
                if (page is not null)
                    sparsePages[i] = (int[])page.Clone();
            }

            return new Storage(
                sparsePages,
                (Entity[])DenseEntities.Clone(),
                (T[])DenseData.Clone(),
                Count);
        }
    }
}

