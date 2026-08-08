using SomeEngine.ECS.Registry;

namespace SomeEngine.ECS.Archetypes;

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
    private readonly SharedColumnMapping[] _sharedColumns;

    public StructuralTransition(Archetype target, SharedColumnMapping[] ownedSharedColumns)
    {
        Target = target;
        _sharedColumns = ownedSharedColumns;
    }

    public ReadOnlySpan<SharedColumnMapping> SharedColumns => _sharedColumns;

    public bool IsIdentityFor(Archetype archetype) => ReferenceEquals(Target, archetype);
}

