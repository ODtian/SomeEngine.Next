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
`Scope.View<T>(in T, ulong)`、`Scope.Key(ulong)`、`Scope.Read<T>(Entity)`、`Scope.Untrack()` 与 `Scope.NextFrame()`。
线程静态的作用域栈保存正在构建的 World 和节点实体。每个构建入口在进入时压栈，
返回或抛出异常时通过 `Dispose` 恢复父作用域。父构建在子构建返回后继续使用自身节点。
成功构建结束时完成子节点匹配与释放；key 分组与节点身份规则见
[[architecture/someui/Node-Identity|Node Identity]]。

`Scope` 在取出当前作用域时调用 `Core.Diagnostics.DevelopmentCheck.Require`，
开发构建中的作用域外调用会报告需要活动构建。shipping 编译按统一开发检查规则移除该调用。
写入与其它依赖来源按对应任务票继续实现。

## 读取作用域实现（#9）

作用域栈帧保存当前 World、视图实体、子声明位置、key 分组与读取登记状态。
`Rerun` 构建开启登记，`Once` 构建使用仅取值状态；嵌套构建返回时同时恢复父视图的登记状态。
读取依赖的缓冲、去重键与逐帧版本比较由
[[architecture/someui/Dependencies|Dependencies]] 维护。

`Scope.Read<T>(Entity)` 只约束 `struct`，按 `default(T) is ISparseComponent` 选择存储。
它返回源组件的 `ref readonly T`，调用方在后续结构修改前消费这份引用。
首次读取造成依赖缓冲添加时，先完成登记，再取得组件引用，使该次读取使用当前存储位置。

## 跨帧作用域（#20）

每个视图构建保存主作用域，并持有需跨帧保留的内容作用域。构建挂起时通过 `Dispose` 恢复父作用域，
重建阶段恢复状态机时重新压入挂起时的声明上下文。子声明位置、key 分组、key 的出现次数
与读取登记状态随构建延续；成功完成整个构建时统一完成子节点匹配与释放。
作用域数据在完成后进入当前线程的复用池，下一次构建初始化为该次 World 和视图实体。

`Scope.NextFrame()` 与异步辅助方法的公开行为见
[[architecture/someui/Async-Build|Async Build]]，阶段顺序见
[[architecture/someui/Rebuild-Schedule|Rebuild Schedule]]。

## 内容作用域实现（#17）

活动构建支持 `Entity.Content()` 的 `ParentScope`。
构建实体与子节点声明的父实体分别保存在上下文中，
容器内容继承当前构建，并在块结束时恢复外层声明上下文。
公开 API、子节点来源与收尾时机见
[[architecture/someui/Child-Source|Child Source]]。

## 存在性与成员读取实现（#12）

当前公开入口增加 `Scope.Has<T>(Entity)` 与 `Scope.Each(QueryHandle)`。
两者共用作用域栈帧的登记状态，`Rerun` 构建记录观察值，`Once` 构建直接取值。
`Each` 返回在当前同步构建中遍历的 `QueryRows`，枚举值为 `Entity`；
每次调用在取得实体序列时完成成员依赖登记。
失效与去重规则由 [[architecture/someui/Dependencies|Dependencies]] 维护，
父列表与 keyed 行的组合见 [[architecture/someui/Lists|Lists]]。

## 局部读取作用域（#15）

`Scope.Untrack()` 返回实现 `IDisposable` 的 `readonly struct UntrackScope`，
按 `using (Scope.Untrack()) { ... }` 使用。进入时保存当前构建的依赖登记状态，
并将该构建切换为仅取值状态；退出时恢复进入前的状态，异常退出也沿 `Dispose` 恢复。

嵌套 `Untrack` 块逐层恢复：内层结束后继续使用外层的仅取值状态，
最外层结束后恢复视图构建原有的状态。`Rerun` 随后读取的数据重新作为依赖登记；
`Once` 随后读取的数据继续使用仅取值状态。

状态由当前作用域栈帧持有。块内当场构建的子视图按自己的 `BuildMode` 设置登记状态，
子构建返回后父构建继续使用进入子构建前的状态。`Untrack` 的有效范围是当前构建，
作用域外调用通过同一 `DevelopmentCheck.Require` 报告。

实现：[Scope](../../../src/SomeEngine.Views/Scope.cs)、
[UntrackScope](../../../src/SomeEngine.Views/UntrackScope.cs)。
公开行为验收见 [UntrackTests](../../../tests/SomeEngine.Views.Tests/UntrackTests.cs)。

## Relations
[[architecture/someui/Dependencies|Dependencies]] · [[architecture/someui/Rebuild-Schedule|Rebuild Schedule]] · [[architecture/someui/Async-Build|Async Build]]
