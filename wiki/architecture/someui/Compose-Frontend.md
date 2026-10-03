# Compose Frontend

## Definition
Compose 写法使用 `[Composable] static` 方法。源生成器为返回 `void` 或 `BuildTask` 的方法生成视图 struct，拦截器把调用点改写为 `Scope.View(new X_View(...), 调用位置 key)`。

```csharp
[Composable]
static void Game(Entity player)
{
    var paused = Remember(static () => MutableStateOf(false));
    Hud(player, paused.Value, v => paused.Value = v);
}
```

## Invariants
- 参数可以是普通值，也可以是 `State<T>`；比较规则见 [[architecture/someui/Build-Modes|Build Modes]]。
- `[Inline]` 的 `[Composable]` 函数形成 key 前缀分组，可以有返回值；分组语义由 #60 接入。
- `Remember` 的值保存在视图实体的状态存储中，按调用位置常量加出现次数识别；调用 `Remember` 的函数必须是 `[Composable]`。
- `MutableStateOf` 产出 `State<T>`。
- `[Composable]` 内容参数由组件调用，作为独立子视图执行，按引用比较；捕获变量的 lambda 允许使用，分析器提示每次调用方重跑都会分配。
- 拦截覆盖 `[Composable]` 调用；函数体内产出节点的调用与其它写法一样获得调用位置常量。

## Current design
分析器要求 `[Composable]` 以源码直接调用的形式使用，方法声明为 `static`，视图结果为 `void` 或 `BuildTask`。方法组、委托、取地址和其它生成器产出的调用由 `SEV008` 报告；声明诊断见 [[architecture/someui/Checks|Checks]]。项目的 `InterceptorsNamespaces` 由 Views 包内 buildTransitive props 和仓库 `Directory.Build.props` 设置。编译通过即每个视图调用都已拦截。

## Compose 视图实现（#58）

`SomeEngine.Views.Compose` 提供方法标记 `ComposableAttribute`，依赖 `SomeEngine.Views`。
`SomeEngine.Views.Generators` 将 Compose 视图声明与普通视图的同名函数放入同一次语义绑定，
从源码调用位置生成拦截器。各种视图继续通过公开的 `Scope.View(in T, ulong)` 声明节点。

```csharp
using SomeEngine.ECS.Entities;
using SomeEngine.Views;
using SomeEngine.Views.Compose;

public static partial class Screens
{
    [Composable]
    public static void Hud(Entity player, bool paused) { /* 声明子视图 */ }
}

// Screens 中生成的视图，调用方也可以通过 Views.Spawn 创建根：
// public readonly record struct Hud_View(Entity player, bool paused)
//     : IView, ISparseComponent
// {
//     public BuildTask Build() { Screens.Hud(player, paused); return default; }
// }
```

- 方法的包含类型和各级外层类型声明为 `partial`，生成视图与原方法位于同一类型中。
  这样视图的 `Build` 能直接使用私有静态方法。`SEV005` 提示该生成接缝。
  包含类型与参数类型的可见范围应允许拦截命名空间引用。
- 单个 Compose 方法的视图名为 `<方法名>_View`，存储为 sparse。
  公开方法生成公开视图，其它方法生成程序集内可用的视图；私有参数类型沿自己的可见范围保留声明。
  主构造函数保留参数名称、类型、默认值和泛型约束，参数由视图组件持有。
  重载方法的视图名添加参数类型签名的 64 位 FNV-1a 后缀，供源码与引用程序集一致定位。
- `void` 方法的 `Build` 调用原方法后返回默认 `BuildTask`；`BuildTask` 方法的 `Build`
  直接返回原结果，使同步完成、构建错误及恢复沿共同的视图构建路径处理。
  生成的 `Build` 对原方法的调用是视图体执行入口；其方法标记将该入口与用户的视图声明调用区分。
- 用户在方法体、普通辅助函数或 lambda 中直接调用 Compose 方法时，拦截器以该调用位置声明独立子视图。
  参数相等时保留已有子内容，参数变化时在同一身份上重建。
  当前 record struct 的相等比较由共同构建路径消费；前端参数比较规则由 #59 接续。
- 消费程序集直接引用定义程序集生成的视图类型。方法的泛型约束、命名参数及可选参数继续由 C# 绑定处理。
  节点的位置 key、出现次数及 `Key` 前缀按 [[architecture/someui/Node-Identity|Node Identity]] 组合。
- `Remember` 与普通参数比较、内联分组分别由 #59、#60 接入。内容函数见下文 #61。

公开编译与行为验收见 `tests/SomeEngine.Views.Generators.Tests/ComposeTests.cs`，
通过生成视图的参数组件、`ElementTree` 子内容、局部状态和 `BuildError` 验证结果。
NativeAOT 验收沿 `tools/SomeEngine.Views.AotSmoke` 使用同一公开入口。

## 内容函数参数（#61）

组件为委托参数标记 `[Composable]`，自行决定调用时机、次数和实参。

```csharp
[Composable]
public static void LazyColumn<T>(IReadOnlyList<T> items, [Composable] Action<T> row)
{
    foreach (T item in items)
        row(item);
}

// 调用方把数据作为显式参数传入，C# 缓存 static lambda 委托：
LazyColumn(items, static item => ItemRow(item));
```

- 内容委托返回 `void` 或 `BuildTask`，实参按值传递；`Action`、`Action<T>`、
  `Func<T, BuildTask>` 及相同形状的自定义委托都沿这一规则生成。
- `row(...)` 和 `row.Invoke(...)` 在生成的构建方法中声明独立子视图。
  该节点保存原内容委托和本次实参，以原源码调用位置与出现次数组合身份。
  条件分支、循环、`Key` 前缀及节点释放沿共同的视图构建路径执行。
- 内容委托在组件参数和内容节点参数中都按引用比较。相同委托与相等实参保留原内容；
  委托引用或实参变化时更新同一节点。父参数变化后，各行继续分别比较自己的参数。
- 生成的构建方法保留源码的命名空间、using、参数、泛型约束和控制流，
  内容参数可以直接转交给另一个 `[Composable]` 组件的 `[Composable]` 参数。
  跨程序集调用继续引用定义程序集生成的视图。
- 内容体的依赖读取、局部状态和 `BuildError` 归内容子视图持有，
  由共同重建路径消费 `BuildTask` 的结果并处理错误恢复。
- Compose 方法和 SwiftUI `Build` 传入捕获外部变量的 lambda 时，`SEV013` 发出 info：
  “调用方每次重跑都会分配”，并建议 `static` lambda 加显式状态参数。
  静态 lambda、仅使用自身参数或常量的 lambda 沿直接表达的数据范围使用。
  编译期可确定为 `Once` 的构建按首次构建处理。
- 容器继续使用 `using (x.Content())`，作用域行为见
  [[architecture/someui/Child-Source|Child Source]]。

公开编译与行为验收见 `tests/SomeEngine.Views.Generators.Tests/ContentFunctionTests.cs`，
以树上内容、参数变化、分支释放、独立依赖和构建错误验证行为。
`tools/SomeEngine.Views.AotSmoke/ComposeChecks.cs` 同时覆盖 NativeAOT 下的内容声明与参数更新。

## Relations
[[architecture/someui/Frontends|Frontends]] · [[architecture/someui/Node-Identity|Node Identity]] · ADR 0013
