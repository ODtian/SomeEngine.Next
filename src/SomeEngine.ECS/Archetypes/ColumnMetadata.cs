using SomeEngine.ECS.Registry;

namespace SomeEngine.ECS.Archetypes;

/// <summary>
/// Archetype 中每一列的元数据。映射 componentId → 列索引 + 操作函数。
/// </summary>
/// <remarks>
/// 设计引用：docs/DESIGN.md §4.1
/// 只为 tableComponentIds（有数据的组件）创建列，Tag 不占列。
/// </remarks>
public readonly struct ColumnMetadata
{
    public readonly int ComponentId;
    public readonly ComponentOperations Operations;

    public ColumnMetadata(int componentId, ComponentOperations operations)
    {
        ComponentId = componentId;
        Operations = operations;
    }
}

