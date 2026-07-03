namespace SomeEngine.ECS.Registry;

/// <summary>
/// 组件存储路径——决定数据存在哪。互斥。
/// </summary>
public enum StoragePath : byte
{
    /// <summary>Table 存储（Archetype Chunk 列）。</summary>
    Table,
    /// <summary>Tag——参与 Archetype identity，不占列空间。</summary>
    Tag,
    /// <summary>SparseSet 侧存储。</summary>
    Sparse,
    /// <summary>Shared Component——World 级中心存储，参与 Archetype identity。</summary>
    Shared,
    /// <summary>关系侧存储。</summary>
    Relation,
    /// <summary>排他关系侧存储。</summary>
    ExclusiveRelation,
}

