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
    public static void Compute(Action run);                                // 登记并立即执行计算
    public static Memo<T> Memo<T>(Func<T> compute);                         // 按读取复核的计算值

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
- `Write`/`ReadWrite` 返回临时副本的引用，计算返回或挂起时比较，值变化才提交并推进版本；引用在当前执行段内经过结构变化仍保持有效。
- `Read`/`Write` 只约束 `struct`，按 `default(T) is ISparseComponent` 选择存储路径，组件改变存储方式不影响界面代码。
- `Ensure` 只在组件不存在时添加，不写入；用于声明行为所需、由系统写入的数据。
- 在计算之外调用作用域函数由开发检查报告；`Depend` 在计算之外为空操作。
- `Build` 不能跨越 `await` 持有 `ref`，由 C# 编译器保证。

## 首次构建实现（#6）

当前公开入口包括 `Scope.World`、`Scope.Entity`、`Scope.View<T>(in T)`、
`Scope.View<T>(in T, ulong)`、`Scope.Key(ulong)`、`Scope.Read<T>(Entity)`、`Scope.Has<T>(Entity)`、
`Scope.Each(QueryHandle)`、`Scope.ReadUp<T>()`、`Scope.Depend(VersionCell)`、`Scope.Compute(Action)`、
`Scope.Memo<T>(Func<T>)`、`Scope.Write<T>(Entity)`、`Scope.ReadWrite<T>(Entity)`、
`Scope.Ensure<T>(Entity)`、`Scope.Untrack()` 与 `Scope.NextFrame()`。
线程静态的作用域栈保存正在构建的 World 和节点实体。每个构建入口在进入时压栈，
返回或抛出异常时通过 `Dispose` 恢复父作用域。父构建在子构建返回后继续使用自身节点。
成功构建结束时完成子节点匹配，外层构建完成后释放旧子树；key 分组与节点身份规则见
[[architecture/someui/Node-Identity|Node Identity]]。

`Scope` 在取出当前作用域时调用 `Core.Diagnostics.DevelopmentCheck.Require`，
开发构建中的作用域外调用会报告需要活动构建。shipping 编译按统一开发检查规则移除该调用。
读取、写入和异步实现见下文。

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
写入副本按每段执行提交并从下一段的当前 ECS 数据重新取得，联合语义见
[[architecture/someui/Async-Build|Async Build]]。
作用域数据在完成后进入当前线程的复用池，下一次构建初始化为该次 World 和视图实体。

`Scope.Cancel` 返回本次视图构建的取消令牌，内容作用域与异步辅助方法共享它。
取消源按首次使用建立，由本次构建持有到状态机结果消费与清理结束。
`Scope.NextFrame()`、构建取消与异步辅助方法的公开行为见
[[architecture/someui/Async-Build|Async Build]]，阶段顺序见
[[architecture/someui/Rebuild-Schedule|Rebuild Schedule]]。
`Scope.ReadUp<T>()` 使用当前视图实体作为环境查找起点，读取路径、缺省值和父链变化的处理见
[[architecture/someui/Dependencies|Dependencies]] 的环境读取依赖。

对象读取通过 `Scope.Depend(VersionCell)` 接入相同的登记状态；`State<T>.Value` 自动登记读值对应的版本。
计算之外的 `Depend` 为空操作。存储和版本规则见 [[architecture/someui/Dependencies|Dependencies]]。

## 计算作用域实现（#16）

`Scope.Compute(Action)` 与 `Scope.Memo<T>(Func<T>)` 沿当前视图登记无节点计算。
计算执行进入自己的读取作用域，`Scope.Entity` 仍为所属视图；嵌套返回后恢复外层。
Memo 当场重算与重建阶段使用同一个进入入口。
列表、版本和生命周期规则由 [[architecture/someui/Computations|Computations]] 维护。

## 组件写入实现（#10）

`Scope.Write<T>(Entity)`、`Scope.ReadWrite<T>(Entity)` 与 `Scope.Ensure<T>(Entity)`
均只约束 `struct`，按 `default(T) is ISparseComponent` 选择存储。

`Write` 在当前计算中按实体和组件类型取得唯一的临时副本。已有组件复制当前值，
缺失组件使用默认值；重复取得的引用指向同一副本。副本由当前计算持有，
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

## 内容作用域实现（#17）

活动构建支持 `Entity.Content()` 的 `ParentScope`。
构建实体与子节点声明的父实体分别保存在上下文中，
容器内容继承当前构建，并在块结束时恢复外层声明上下文。
`Content` 与嵌套 `Content` 继承调用方计算的同一份组件副本，块结束时完成子节点声明，
调用方计算成功结束时统一比较提交。嵌套视图的 `Build` 单独持有自己的副本，
`ChildSource.Caller` 的构建也在成功结束时提交自身数据。
副本缓冲在当前计算首次取得写引用时建立，内容作用域使用该计算已有的缓冲。
公开验收见 [ContentWriteTests](../../../tests/SomeEngine.Views.Tests/ContentWriteTests.cs)。
内容块与外层构建共用当次子声明修改记录，外层失败时一同恢复。
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
