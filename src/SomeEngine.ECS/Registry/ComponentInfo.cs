using SomeEngine.ECS.Owners;

namespace SomeEngine.ECS.Registry;

/// <summary>
/// 单个组件类型的非泛型元数据记录。
/// </summary>
internal struct ComponentInfo
{
    public int Id;
    public Type Type;
    public int Size;
    public StoragePath Storage;
    public bool ContainsReferences;
    internal bool IsJobAliasFree;
    internal IHierarchyComponentRegistration? HierarchyRegistration;
    internal ComponentOperations Operations;

    // ——— Table 分支正交标志 ———

    /// <summary>Cleanup 生命周期（DestroyEntity 时保留）。仅 Table 有效。</summary>
    public bool IsCleanup;
    /// <summary>Native Removed&lt;T&gt; history; it does not own entity lifetime on Destroy.</summary>
    public bool IsRemovedFact;
    /// <summary>有 per-chunk enable bit mask。仅 Table 有效。</summary>
    public bool IsEnableable;
    /// <summary>有倒排索引。仅 Table 有效。</summary>
    public bool IsIndexed;

    /// <summary>Canonical relationship value. Only valid for Table components.</summary>
    public bool IsRelationshipSource;

    /// <summary>Derived relationship value. Only valid for Table components.</summary>
    public bool IsRelationshipTarget;

    /// <summary>Internal header or inline column belonging to one logical dynamic buffer.</summary>
    internal bool IsBufferStorage;

    /// <summary>Whether public generic APIs may add or remove this component.</summary>
    public bool AllowsPublicStructuralMutation;

    /// <summary>Whether public unscoped APIs may return writable data or replace this component.</summary>
    public bool AllowsPublicValueMutation;

}

