# Computations

## Definition
计算是会因依赖变化而重新执行的代码及其依赖记录。

| 计算 | 是否有自身节点 |
|---|---|
| 视图的 `Build` | 有，挂在视图实体上 |
| Solid 的 Effect、Memo、属性绑定 | 没有，存于所属视图实体的计算列表（sparse 组件） |

## Invariants
- 两种计算共用同一套依赖登记、版本检查与调度。
- 属性绑定写目标节点组件副本的目标字段，例如 `Write<Text>(node).Value = ...`；计算结束时与当前值比较提交，值相同则保留版本。提交规则见 [[architecture/someui/Build-Scope|Build Scope]]。
- Memo 在被读取时检查自身依赖，过期则在嵌套作用域中当场重算，外层只依赖 Memo 本身；结果相等时不使下游失效。
- 在某个计算中创建的计算随它释放。

## 无节点计算实现（#16）

```csharp
public static class Scope
{
    public static void Compute(Action run);
    public static Memo<T> Memo<T>(Func<T> compute);
}

public sealed class Memo<T> : VersionCell
{
    public T Value { get; }
}
```

`Scope.Compute` 在当前视图实体登记计算，并立即执行一次。该次读取属于计算自己的依赖，
后续变化由 `ViewRebuildSystem` 重跑该计算；所属视图的 `Build` 保留自己的依赖与构建结果。
`Once` 构建与 `Untrack` 块中创建的计算也开启自己的依赖登记。
在计算内部使用 `Untrack` 时，该块的读取沿正常作用域规则仅取值。

视图实体的 sparse `Computations` 组件持有唯一的计算列表。列表按创建顺序保存回调及其读取数据，
每个计算记录创建它的计算。创建者重跑前释放之前的全部后代计算，随后登记这次创建的计算；
视图 `Build` 重跑前释放该视图的计算列表。视图从树上释放后，重建调度消费当前存活节点。
嵌套计算返回后，`Scope.World`、`Scope.Entity` 与依赖登记恢复到外层计算。

`Scope.Memo` 在嵌套作用域中得到初始结果。之后读取 `Memo.Value` 时复核其自身依赖，
来源变化就当场重算，包括同一帧已经完成的状态或组件写入。调用方登记的对象来源为这个 Memo，
链式 Memo 逐层复核自己的来源。逐帧对象依赖检查也先刷新被依赖的 Memo，再比较其版本，
使相等结果保留已有下游显示。

Memo 使用 `EqualityComparer<T>.Default` 比较结果。结果相等时保留原值与版本；结果变化时
调用 `VersionCell.Advance()`。Memo 的生命周期随创建者和所属视图，开发构建通过
Core 的 `DevelopmentCheck` 报告过期 Memo 的读取、递归读取以及作用域外的创建。

计算通过 `Scope.Write` 写节点数据。例如在构建中登记以下绑定，`count` 的变化会更新
已经带有 `Count` 组件的节点：

```csharp
Entity node = Scope.Entity;
State<int> count = CountState;
Scope.Compute(() => Scope.Write<Count>(node).Value = count.Value);
```

`Once` 视图的计算需要读取最新参数时，使用 `Scope.Read<Self>(Scope.Entity)`；闭包捕获的
构建局部值遵循 C# 的值复制规则。

## 计算异常

立即执行的计算和 Memo 求值异常沿调用方传播，构建边界将其记为所属视图的 `BuildError`。
重建阶段的计算异常沿相同记录与开发报告入口隔离，继续同节点后续计算与其它视图。
计算保留这次实际读取的依赖，来源变化后重新尝试；成功计算清除与它此前异常对应的错误。

Memo 求值失败时保留上次成功结果与版本，并在读取时传播原异常。
对象依赖同时观察该来源的失败与恢复，使恢复后的相等结果也能让失败的读取者完成更新。
版本的推进仍由结果相等比较决定。

实现：[计算列表](../../../src/SomeEngine.Views/Computations.cs)、
[Scope](../../../src/SomeEngine.Views/Scope.cs)、[Memo](../../../src/SomeEngine.Views/Memo.cs)。
公开验收：[ComputationTests](../../../tests/SomeEngine.Views.Tests/ComputationTests.cs)，
覆盖绑定与构建的独立更新、嵌套释放、组件读取、相等结果、同帧读取、链式 Memo、
Once 参数、Untrack、异常恢复与当帧收敛。
[ComputationChecks](../../../tests/SomeEngine.Views.Tests/ComputationChecks.cs) 通过
`just ecs-aot win-x64` 复用相同公开行为，并验证托管回收后的绑定与 Memo。

## Relations
[[architecture/someui/Build-Modes|Build Modes]] · [[architecture/someui/Solid-Frontend|Solid Frontend]] · [[architecture/someui/Rebuild-Schedule|Rebuild Schedule]]
