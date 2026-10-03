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

## 首次构建实现（#6）

当前公开入口为 `Scope.World`、`Scope.Entity`、`Scope.View<T>(in T)`、
`Scope.View<T>(in T, ulong)`、`Scope.Key(ulong)` 与 `Scope.Read<T>(Entity)`。
线程静态的作用域栈保存正在构建的 World 和节点实体。每个构建入口在进入时压栈，
返回或抛出异常时通过 `Dispose` 恢复父作用域。父构建在子构建返回后继续使用自身节点。
成功构建结束时完成子节点匹配与释放；key 分组与节点身份规则见
[[architecture/someui/Node-Identity|Node Identity]]。

`Scope` 在取出当前作用域时调用 `Core.Diagnostics.DevelopmentCheck.Require`，
开发构建中的作用域外调用会报告需要活动构建。shipping 编译按统一开发检查规则移除该调用。
后续读取与写入实现见下文；其它依赖来源与异步入口按对应任务票继续实现。

## 读取作用域实现（#9）

作用域栈帧保存当前 World、视图实体、子声明位置、key 分组与读取登记状态。
`Rerun` 构建开启登记，`Once` 构建使用仅取值状态；嵌套构建返回时同时恢复父视图的登记状态。
读取依赖的缓冲、去重键与逐帧版本比较由
[[architecture/someui/Dependencies|Dependencies]] 维护。

`Scope.Read<T>(Entity)` 只约束 `struct`，按 `default(T) is ISparseComponent` 选择存储。
它返回源组件的 `ref readonly T`，调用方在后续结构修改前消费这份引用。
首次读取造成依赖缓冲添加时，先完成登记，再取得组件引用，使该次读取使用当前存储位置。

## 组件写入实现（#10）

`Scope.Write<T>(Entity)`、`Scope.ReadWrite<T>(Entity)` 与 `Scope.Ensure<T>(Entity)`
均只约束 `struct`，按 `default(T) is ISparseComponent` 选择存储。

`Write` 在当前计算中按实体和组件类型取得唯一的临时副本。已有组件复制当前值，
缺失组件使用默认值；重复取得的引用指向同一副本。副本由当前作用域持有，
所以表格迁移、sparse 数组扩展、继续取得其它写引用和嵌套子构建都保持引用有效。
`Write` 的取值与写入保持仅取值状态，读取依赖通过 `Read` 或 `ReadWrite` 登记。

成功构建完成子节点声明后，逐项与提交时的当前组件值比较。
已有且相等的值保留实体写版本和组件类型写版本；不同值通过公开
`World.Replace` / `ReplaceSparse` 提交，缺失组件通过 `World.Add` / `AddSparse` 添加，
包括声明默认值的情况。每个计算单独完成自己的提交，嵌套子计算返回后恢复父计算的副本。
构建抛出异常时释放该次副本，已有组件保持提交前的值。

`ReadWrite` 先通过 `Ensure` 添加缺失组件，再执行 `Read` 登记当前版本，
最后返回 `Write` 的副本引用。`Ensure` 即时添加缺失的默认组件，
已有组件保留值与写版本。它适用于输入系统等其它系统持有数据的声明。

现有 ECS 的 `Add`、`Replace`、`AddSparse`、`ReplaceSparse` 与 `HasSparse`
接受 `struct` 泛型以支持这一存储选择；表格与 sparse 标记要求由统一开发检查报告。
提交复用各公开入口的访问权限、组件约束、钩子和版本规则。

实现：[Scope](../../../src/SomeEngine.Views/Scope.cs)、
[组件副本](../../../src/SomeEngine.Views/ComponentCopy.cs)。
公开验收：[ComponentWriteTests](../../../tests/SomeEngine.Views.Tests/ComponentWriteTests.cs)、
[共享写入验收](../../../tests/SomeEngine.Views.Tests/ComponentWriteChecks.cs)。

## Relations
[[architecture/someui/Dependencies|Dependencies]] · [[architecture/someui/Rebuild-Schedule|Rebuild Schedule]] · [[architecture/someui/Async-Build|Async Build]]
