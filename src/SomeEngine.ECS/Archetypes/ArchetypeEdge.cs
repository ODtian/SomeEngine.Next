using SomeEngine.ECS.Registry;

namespace SomeEngine.ECS.Archetypes;

/// <summary>
/// Archetype 迁移边缓存条目。记录目标 Archetype 和预计算的列映射。
/// </summary>
/// <remarks>
/// 设计引用：docs/DESIGN.md §4.1, §5.2
/// </remarks>
internal readonly struct ArchetypeEdge
{
    private readonly StructuralTransition _plan;

    public Archetype Target => _plan.Target;
    public SharedColumnMapping[] SharedColumns => _plan.SharedColumns;

    public ArchetypeEdge(Archetype target, SharedColumnMapping[] sharedColumns)
    {
        _plan = new StructuralTransition(target, sharedColumns);
    }

    public StructuralTransition AsTransition() => _plan;
}

/// <summary>
/// 预计算的列映射：source 和 destination archetype 之间共享列的对应关系。
/// </summary>
internal readonly struct SharedColumnMapping
{
    /// <summary>源 Archetype 中的列索引。</summary>
    public readonly int SourceColumnIndex;
    /// <summary>目标 Archetype 中的列索引。</summary>
    public readonly int DestinationColumnIndex;
    /// <summary>该列的操作函数指针。</summary>
    public readonly ComponentOperations Operations;

    public SharedColumnMapping(int sourceColumnIndex, int destinationColumnIndex, ComponentOperations operations)
    {
        SourceColumnIndex = sourceColumnIndex;
        DestinationColumnIndex = destinationColumnIndex;
        Operations = operations;
    }
}

internal readonly struct StructuralTransition
{
    public readonly Archetype Target;
    public readonly SharedColumnMapping[] SharedColumns;

    public StructuralTransition(Archetype target, SharedColumnMapping[] sharedColumns)
    {
        Target = target;
        SharedColumns = sharedColumns;
    }

    public bool IsIdentityFor(Archetype archetype) => ReferenceEquals(Target, archetype);
}

