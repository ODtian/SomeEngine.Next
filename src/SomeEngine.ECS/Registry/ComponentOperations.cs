using System.Runtime.CompilerServices;

namespace SomeEngine.ECS.Registry;

/// <summary>
/// 组件操作函数指针表。每种组件类型在注册时生成一组操作函数指针，消除运行时虚调用。
/// </summary>
/// <remarks>
/// 设计引用：docs/DESIGN.md §2.3
/// Table-backed columns are represented as T[].
/// </remarks>
public unsafe struct ComponentOperations
{
    /// <summary>
    /// 将 source[sourceIndex] 拷贝到 destination[destinationIndex]。用于 archetype 迁移时的列拷贝。
    /// </summary>
    public delegate*<object, int, object, int, void> CopyElement;

    /// <summary>
    /// 将 array[lastIndex] 覆盖到 array[removeIndex]。用于 chunk 内 swap-remove。
    /// </summary>
    public delegate*<object, int, int, void> SwapRemove;

    /// <summary>
    /// 创建列存储对象。返回 T[capacity]。
    /// </summary>
    public delegate*<int, object> CreateArray;
}

