# SomeEngine.Views.Compose

`[Composable]` 静态方法表达一个带独立节点的视图，方法参数作为 sparse 组件保存在该节点上。
运行库依赖 `SomeEngine.Views`，生成器和 buildTransitive props 随 Views 包依赖接入，
提供视图生成、调用位置拦截及拦截命名空间配置。

```csharp
using SomeEngine.ECS.Entities;
using SomeEngine.Views;
using SomeEngine.Views.Compose;
using static SomeEngine.Views.Compose.Compose;

public static partial class Screens
{
    [Composable]
    public static void Game(Entity player)
    {
        var paused = Remember(static () => MutableStateOf(false));
        Hud(player, paused.Value, value => paused.Value = value);
    }

    [Composable]
    public static void Hud(Entity player, bool paused, Action<bool> change) { /* 声明子视图 */ }
}

// 根视图：Views.Spawn(world, Entity.Null, new Screens.Game_View(player));
// 子视图：在 Build 中直接调用 Screens.Game(player)。
```

包含类型与各级外层类型使用 `partial`，生成的 `<方法名>_View` 位于原方法的包含类型中。
视图主构造函数保留参数名称、类型、可选值和泛型约束；同名重载通过参数类型签名后缀区分视图类型。
公开方法生成公开视图，私有和程序集内方法生成程序集内视图。
可拦截调用的包含类型与参数类型应能从 `SomeEngine.Views.Generated` 命名空间引用。

方法返回 `void` 或 `BuildTask`。生成的 `Build` 执行视图体，调用方的直接调用声明视图。
相等参数保持已有内容，变化参数更新该节点并重建内容，构建错误使用共同的 `BuildError` 与开发检查入口。
消费程序集引用定义程序集已生成的视图类型。

`Remember<T>(Func<T> init)` 由调用位置 key 和出现次数定位视图实体上的值。
值保存在托管 sparse 组件中，条件分支再次出现时恢复原值，视图释放时随 ECS 释放。
循环按出现次数区分值，`Scope.Key` 为每项提供独立前缀。
`MutableStateOf<T>(T value)` 创建通用 `State<T>`，推荐以上面的 static 初始化器保存局部状态。
普通值作为子参数时，状态读取使父视图刷新；传递 `State<T>` 时，读取其 `Value` 的视图刷新。

生成视图的值类型、record 与 `string` 参数按值相等规则比较，其它类按引用比较。
`Action`/`Func` 回调在比较中省略，参数组件始终更新为最新值，保留最新捕获内容。
带 `[Composable]` 标记的内容参数按委托引用比较。生成的哈希与相等规则一致。

`SEV005` 提示 partial 接缝，`SEV006` 检查 static，`SEV007` 检查视图结果类型，
`SEV008` 检查方法组、委托、取地址、生成代码调用与实际拦截结果。
`SEV009` 要求调用 `Remember` 的方法或 lambda 使用自己的 `[Composable]` 标记。
Views 包的 buildTransitive props 提供拦截命名空间，`SEV001` 检查该配置。

组件通过 `[Composable]` 内容委托参数控制子视图的声明时机、次数和实参：

```csharp
[Composable]
public static void LazyColumn<T>(IReadOnlyList<T> items, [Composable] Action<T> row)
{
    foreach (T item in items)
        row(item);
}

// LazyColumn(items, static item => ItemRow(item));
```

内容函数的直接调用及 `.Invoke` 各自形成带原委托与实参的独立子视图，委托按引用比较。
相同的 `static` lambda 与相等实参保留已有子内容；实参或委托引用变化时更新对应子视图。
内容读取的状态和构建错误归该子视图持有，组件之间可以继续转交原内容委托。
委托返回 `void` 或 `BuildTask`，参数按值传递，声明形状由 `SEV014` 检查。
`SEV013` 在 Compose 或 SwiftUI 构建体传入捕获 lambda 时提示“调用方每次重跑都会分配”，
推荐使用 `static` lambda 并把状态作为显式参数传入。
