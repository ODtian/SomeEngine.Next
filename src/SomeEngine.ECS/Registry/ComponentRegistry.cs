using SomeEngine.ECS.Collections;

namespace SomeEngine.ECS.Registry;

/// <summary>
/// 非泛型全局组件元数据注册表。按 componentId 索引。
/// </summary>
/// <remarks>
/// 设计引用：docs/DESIGN.md §2.3
/// </remarks>
internal static class ComponentRegistry
{
    private static readonly Lock s_gate = new();
    private static ComponentInfo[] _infos = new ComponentInfo[16];
    private static int _count;

    /// <summary>
    /// 注册组件元数据。由 ComponentMetadata&lt;T&gt; 的 static ctor 调用。
    /// </summary>
    internal static void Register(int id, ComponentInfo info)
    {
        lock (s_gate)
        {
            EnsureCapacity(id + 1);
            _infos[id] = info;
            if (id >= _count)
                _count = id + 1;
        }
    }

    /// <summary>
    /// 按 ID 查找组件元数据。返回 ref 以避免拷贝。
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">id 无效时。</exception>
    public static ref ComponentInfo Get(int id)
    {
        int count = Volatile.Read(ref _count);
        var infos = _infos;

        if (id <= 0 || id >= count)
            throw new ArgumentOutOfRangeException(nameof(id),
                $"Component ID {id} is not registered. Valid range: [1, {count - 1}].");

        return ref infos[id];
    }

    /// <summary>
    /// 当前已注册的组件数量。
    /// </summary>
    public static int Count
    {
        get
        {
            int count = Volatile.Read(ref _count);
            return count > 0 ? count - 1 : 0;
        }
    }

    private static void EnsureCapacity(int required)
    {
        ArrayGrowthExtensions.EnsureCapacity(ref _infos, required, 16);
    }
}

