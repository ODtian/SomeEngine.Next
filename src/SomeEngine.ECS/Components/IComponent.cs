namespace SomeEngine.ECS.Components;

/// <summary>
/// Tag 组件——参与 Archetype identity，不占 Chunk 列空间，无数据。
/// </summary>
public interface ITag { }

/// <summary>
/// Canonical value of an ECS relationship.
/// </summary>
/// <remarks>
/// Relationship sources are readable and participate in ordinary change queries. Their writable
/// query capability is reserved for an owner-bound deferred writer; generic component mutation
/// APIs are not a valid relationship mutation surface.
/// </remarks>
public interface IRelationshipSource : SomeEngine.ECS.IComponent { }

/// <summary>
/// Read-only value derived from one or more relationship sources.
/// </summary>
/// <remarks>
/// Relationship targets may be matched, read, and observed through ordinary change queries, but
/// only the ECS relationship maintenance kernel may mutate them.
/// </remarks>
public interface IRelationshipTarget : SomeEngine.ECS.IComponent { }

/// <summary>
/// Removed table component fact retained until the world clears it.
/// </summary>
public struct Removed<T> : SomeEngine.ECS.ICleanupComponent
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
/// 索引组件——Table 存储 + 倒排索引。
/// </summary>
public interface IIndexedComponent : SomeEngine.ECS.IComponent { }

/// <summary>
/// 索引组件——Table 存储 + 倒排索引。
/// </summary>
public interface IIndexedComponent<TKey> : IIndexedComponent where TKey : notnull, IEquatable<TKey>
{
    TKey GetKey();
}



