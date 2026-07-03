using System.Runtime.CompilerServices;
using SomeEngine.ECS.Archetypes;
using SomeEngine.ECS.Collections;
using SomeEngine.ECS.Serialization;

namespace SomeEngine.ECS.Entities;

/// <summary>
/// Entity 分配/回收/查找存储。使用 free-list 复用已释放的 index。
/// </summary>
/// <remarks>
/// 设计引用：docs/DESIGN.md §3.2
/// - Index=0 永不分配，records[0] 始终未使用
/// - Free-list 嵌入 EntityRecord：死亡后 FreeListNext = nextFreeIndex
/// - Generation 嵌入 EntityRecord：生存检查和位置查询共享同一 cache line
/// </remarks>
internal class EntityStore
{
    private const int SerializationReservedSlot = int.MinValue;

    private EntityRecord[] _records;
    private int _freeListHead = -1;
    private int _aliveCount;
    private int _count; // 已使用的最大 index（含已回收的）

    internal EntityStore(int initialCapacity = 64)
    {
        // +1 因为 index 0 保留
        _records = new EntityRecord[initialCapacity + 1];
        // Generation[0] 初始为 -1，确保 default(Entity) 永远不 alive
        _records[0].Generation = -1;
    }

    /// <summary>
    /// 分配一个新 entity，返回真实 Entity。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal Entity Allocate()
    {
        ref var record = ref Allocate(out var id);
        _ = record;
        return id;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref EntityRecord Allocate(out Entity id)
    {
        int index;
        if (_freeListHead != -1)
        {
            // 从 free list pop
            index = _freeListHead;
            int nextFree = _records[index].FreeListNext;
            int gen = _records[index].Generation; // 保存当前代数
            _records[index] = default; // 清除 free-list 数据
            _records[index].Generation = gen; // 恢复代数
            _freeListHead = nextFree;
        }
        else
        {
            // 扩展：从 _count+1 开始（跳过 0）
            _count++;
            index = _count;
            EnsureCapacity(index + 1);
            // Generation 初始为 0
        }

        _aliveCount++;
        id = new Entity(index, _records[index].Generation);
        return ref _records[index];
    }

    /// <summary>
    /// 释放一个 entity。校验 IsAlive，否则抛异常。
    /// </summary>
    internal void Free(Entity id)
    {
        if (!IsAlive(id))
            throw new InvalidOperationException(
                $"Cannot free {id}: entity is not alive (possibly already freed or stale generation).");

        int index = id.Index;
        // Generation 递增，使旧 Entity 失效
        int nextGen = _records[index].Generation + 1;
        _aliveCount--;
        // 推入 free list
        _records[index].Archetype = null;
        _records[index].Chunk = null;
        _records[index].FreeListNext = _freeListHead;
        _records[index].RowInChunk = 0;
        _records[index].Generation = nextGen;
        _freeListHead = index;
    }

    /// <summary>
    /// 检查 entity 是否存活。
    /// </summary>
    internal bool IsAlive(Entity id)
    {
        return id.Index > 0
            && id.Index <= _count
            && _records[id.Index].Generation == id.Generation;
    }

    /// <summary>
    /// 获取 entity 的记录引用。校验 IsAlive，否则抛异常。
    /// </summary>
    internal ref EntityRecord GetRecord(Entity id)
    {
        if (!IsAlive(id))
            throw new InvalidOperationException(
                $"Cannot access record for {id}: entity is not alive.");

        return ref _records[id.Index];
    }

    /// <summary>
    /// 当前已分配的最大 index 数（含已回收的）。
    /// </summary>
    internal int Count => _count;

    internal int AliveCount => _aliveCount;

    internal void EnsureAdditionalCapacity(int additionalCount)
    {
        if (additionalCount < 0)
            throw new ArgumentOutOfRangeException(nameof(additionalCount));

        EnsureCapacity(_count + additionalCount + 1);
    }

    internal bool TryAllocateContiguous(int count, out int firstIndex)
    {
        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count));

        if (_freeListHead != -1)
        {
            firstIndex = 0;
            return false;
        }

        EnsureCapacity(_count + count + 1);
        firstIndex = _count + 1;
        _count += count;
        _aliveCount += count;
        return true;
    }

    internal void InitializeContiguousRecords(
        Archetype archetype,
        Chunk chunk,
        int startRow,
        int count,
        int firstIndex)
    {
        var records = _records;
        var entities = chunk.Entities;
        for (int i = 0; i < count; i++)
        {
            int entityIndex = firstIndex + i;
            int row = startRow + i;
            entities[row] = new Entity(entityIndex, generation: 0);
            records[entityIndex].Archetype = archetype;
            records[entityIndex].Chunk = chunk;
            records[entityIndex].RowInChunk = row;
        }
    }

    internal int GetGeneration(int index)
    {
        if (!IsAllocatedIndex(index))
            throw new ArgumentOutOfRangeException(nameof(index));

        return _records[index].Generation;
    }

    internal bool IsAliveIndex(int index)
    {
        if (!IsAllocatedIndex(index))
            return false;

        return _records[index].Archetype is not null;
    }

    internal void ResetForSerialization(int maxIndex, IReadOnlyList<EntitySlotSnapshot> slots)
    {
        if (maxIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(maxIndex));

        _records = new EntityRecord[Math.Max(64, maxIndex + 1)];
        _records[0].Generation = -1;
        _count = maxIndex;
        _aliveCount = 0;
        _freeListHead = -1;

        if (slots.Count != maxIndex)
            throw new InvalidOperationException("Serialized entity slots must be dense from index 1 to maxIndex.");

        for (int i = maxIndex; i >= 1; i--)
        {
            var slot = slots[i - 1];
            if (slot.Index != i)
                throw new InvalidOperationException($"Expected serialized entity slot index {i}, found {slot.Index}.");
            if (slot.Generation < 0)
                throw new InvalidOperationException($"Invalid serialized entity slot generation {slot.Generation}.");

            _records[i].Generation = slot.Generation;
            if (slot.IsAlive)
            {
                _records[i].FreeListNext = SerializationReservedSlot;
            }
            else
            {
                _records[i].FreeListNext = _freeListHead;
                _freeListHead = i;
            }
        }
    }

    internal ref EntityRecord AllocatePreserved(Entity id)
    {
        if (!IsAllocatedIndex(id.Index))
            throw new InvalidOperationException($"Cannot restore {id}: index is outside the prepared entity store.");

        if (_records[id.Index].Archetype is not null)
            throw new InvalidOperationException($"Cannot restore {id}: slot is already alive.");

        if (_records[id.Index].Generation != id.Generation)
            throw new InvalidOperationException(
                $"Cannot restore {id}: serialized slot generation is {_records[id.Index].Generation}.");

        if (_records[id.Index].FreeListNext != SerializationReservedSlot)
            throw new InvalidOperationException($"Cannot restore {id}: slot is not marked alive in serialized slots.");

        _records[id.Index] = default;
        _records[id.Index].Generation = id.Generation;
        _aliveCount++;
        return ref _records[id.Index];
    }

    private void EnsureCapacity(int required)
    {
        ArrayGrowthExtensions.EnsureCapacity(ref _records, required, 1);
    }

    private bool IsAllocatedIndex(int index) => index > 0 && index <= _count;
}

