# Build Scope

## Definition
`Scope` 是当前正在执行的计算的上下文，以线程静态变量隐式提供。构建函数是静态方法，`Build` 不接收上下文参数。

```csharp
public static class Scope
{
    public static World World { get; }
    public static Entity Entity { get; }
    public static CancellationToken Cancel { get; }

    public static ref readonly T Read<T>(Entity entity) where T : struct;   // 登记依赖
    public static bool Has<T>(Entity entity) where T : struct;              // 结构依赖
    public static QueryRows Each(QueryHandle query);                        // 查询成员依赖
    public static ref readonly T ReadUp<T>() where T : struct;              // 祖先环境数据
    public static void Depend(VersionCell cell);                            // 对象依赖

    public static ref T Write<T>(Entity entity) where T : struct;           // 不登记依赖
    public static ref T ReadWrite<T>(Entity entity) where T : struct;
    public static void Ensure<T>(Entity entity) where T : struct;           // 声明需要该组件

    public static Entity View<T>(in T view) where T : struct, IView, IEquatable<T>;
    public static KeyScope Key(ulong key);
    public static UntrackScope Untrack();                                   // using 块内不登记依赖
    public static FrameAwaitable NextFrame();
}
```

## Invariants
- 作用域由视图重建系统在执行每个计算前后设置与恢复，并维护作用域栈（内联构建子视图、Memo 当场重算时嵌套）。
- 当前计算处于不登记依赖状态时（`Once` 模式的 `Build`、`Untrack` 内），读取不登记依赖。
- `Write`/`ReadWrite` 返回临时副本的引用，计算结束时比较，值变化才提交并推进版本；返回的引用在整个构建期间不会被结构变化移动。
- `Read`/`Write` 只约束 `struct`，按 `default(T) is ISparseComponent` 选择存储路径，组件改变存储方式不影响界面代码。
- `Ensure` 只在组件不存在时添加，不写入；用于声明行为所需、由系统写入的数据。
- 在计算之外调用作用域函数由开发检查报告；`Depend` 在计算之外为空操作。
- `Build` 不能跨越 `await` 持有 `ref`，由 C# 编译器保证。

## Relations
[[architecture/someui/Dependencies|Dependencies]] · [[architecture/someui/Rebuild-Schedule|Rebuild Schedule]] · [[architecture/someui/Async-Build|Async Build]]
