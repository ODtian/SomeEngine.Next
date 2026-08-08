using System.Runtime.CompilerServices;

namespace SomeEngine.ECS.Registry;

/// <summary>
/// 组件操作函数指针表。每种组件类型在注册时生成一组操作函数指针，消除运行时虚调用。
/// </summary>
/// <remarks>
/// 设计引用：docs/DESIGN.md §2.3
/// Table-backed columns are represented as T[].
/// </remarks>
internal unsafe struct ComponentOperations
{
    /// <summary>
    /// Returns a managed interior reference to one row without exposing the owning column array.
    /// </summary>
    internal delegate*<object, int, ref byte> GetReference;

    /// <summary>
    /// Copies one component value between managed interior references. The generic implementation
    /// performs a typed assignment so reference-bearing component fields remain GC-correct.
    /// </summary>
    internal delegate*<ref byte, ref byte, void> CopyValue;

    /// <summary>
    /// 将 array[lastIndex] 覆盖到 array[removeIndex]。用于 chunk 内 swap-remove。
    /// </summary>
    internal delegate*<object, int, int, void> SwapRemove;

    /// <summary>
    /// 创建列存储对象。返回 T[capacity]。
    /// </summary>
    internal delegate*<int, object> CreateArray;
}

