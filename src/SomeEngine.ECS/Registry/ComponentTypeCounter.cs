using System.Threading;

namespace SomeEngine.ECS.Registry;

/// <summary>
/// 全局原子组件 ID 计数器。Id 从 1 开始，0 保留为无效值。
/// </summary>
internal static class ComponentTypeCounter
{
    private static int _nextId;

    /// <summary>
    /// 分配下一个全局唯一组件 ID。线程安全。
    /// </summary>
    internal static int Next()
    {
        return Interlocked.Increment(ref _nextId);
    }
}

