namespace SomeEngine.ECS.Components;

/// <summary>
/// Table 存储组件（Archetype Chunk 列）。
/// </summary>
public interface IComponent : SomeEngine.ECS.IComponent { }

/// <summary>
/// Tag 组件——参与 Archetype identity，不占 Chunk 列空间，无数据。
/// </summary>
public interface ITag { }

/// <summary>
/// 可启用/禁用组件——Table 存储 + per-chunk enable bit mask。
/// </summary>
public interface IEnableableComponent : IComponent, SomeEngine.ECS.IEnableableComponent { }

/// <summary>
/// Cleanup 组件——Table 存储，DestroyEntity 时保留，直到所有 cleanup 组件被显式移除。
/// </summary>
public interface ICleanupComponent : IComponent, SomeEngine.ECS.ICleanupComponent { }

/// <summary>
/// Removed table component fact retained until the world clears it.
/// </summary>
public struct Removed<T> : ICleanupComponent
    where T : struct, SomeEngine.ECS.IComponent
{
    public T Value;
    public uint Version;
}

/// <summary>
/// SparseSet 侧存储组件——不参与 Archetype identity。
/// </summary>
public interface ISparseComponent { }

/// <summary>
/// 关系组件——侧存储，一个 entity 可有多条同类型关系。
/// </summary>
public interface IRelation { }

/// <summary>
/// 排他关系——每个 source entity 只能有一个同类型关系。
/// </summary>
public interface IExclusiveRelation : IRelation { }

/// <summary>
/// 索引组件——Table 存储 + 倒排索引。
/// </summary>
public interface IIndexedComponent : IComponent { }

/// <summary>
/// 索引组件——Table 存储 + 倒排索引。
/// </summary>
public interface IIndexedComponent<TKey> : IIndexedComponent where TKey : notnull, IEquatable<TKey>
{
    TKey GetKey();
}



