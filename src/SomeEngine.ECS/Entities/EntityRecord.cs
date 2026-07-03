using SomeEngine.ECS.Archetypes;

namespace SomeEngine.ECS.Entities;

/// <summary>
/// 实体记录——存储 entity 在 archetype/chunk 中的位置 + 代数。
/// </summary>
/// <remarks>
/// 设计引用：docs/DESIGN.md §3.2
/// - 活着的 entity 通过 Chunk 引用直接定位，消除 list 索引查找
/// - Free-list 嵌入：死亡后 Archetype = null，FreeListNext = nextFreeIndex
/// - Generation 与位置在同一 struct 里，减少 cache miss
/// </remarks>
internal struct EntityRecord
{
    /// <summary>
    /// entity 所在的 Archetype。null = 未归位（CommandBuffer 预分配态或 free-list 中）。
    /// </summary>
    public Archetype? Archetype;

    /// <summary>
    /// entity 所在的 Chunk 引用。活着的 entity 通过此字段直接访问 chunk。
    /// </summary>
    public Chunk? Chunk;

    /// <summary>
    /// free-list 链接。死亡后复用为 nextFreeIndex。活着时无意义。
    /// </summary>
    public int FreeListNext;

    /// <summary>
    /// entity 在 Chunk 内的行号。
    /// </summary>
    public int RowInChunk;

    /// <summary>
    /// entity 代数。每次释放递增，使旧 Entity 失效。
    /// </summary>
    public int Generation;

    /// <summary>
    /// Hierarchy Parent dirty queue generation. 0 means not queued.
    /// </summary>
    public uint ParentDirtyVersion;
}

