namespace SomeEngine.ECS.Registry;

/// <summary>
/// 单个组件类型的非泛型元数据记录。
/// </summary>
public struct ComponentInfo
{
    public int Id;
    public Type Type;
    public int Size;
    public StoragePath Storage;
    public bool ContainsReferences;
    public ComponentOperations Operations;

    // ——— Table 分支正交标志 ———

    /// <summary>Cleanup 生命周期（DestroyEntity 时保留）。仅 Table 有效。</summary>
    public bool IsCleanup;
    /// <summary>有 per-chunk enable bit mask。仅 Table 有效。</summary>
    public bool IsEnableable;
    /// <summary>有倒排索引。仅 Table 有效。</summary>
    public bool IsIndexed;

    /// <summary>Relation 侧存储为 query 过滤自动生成的 tag。</summary>
    public bool IsRelationTag;

}

