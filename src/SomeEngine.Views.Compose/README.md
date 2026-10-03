# SomeEngine.Views.Compose

`[Composable]` 静态方法表达一个带独立节点的视图，方法参数作为 sparse 组件保存在该节点上。
运行库依赖 `SomeEngine.Views`，生成器和 buildTransitive props 随 Views 包依赖接入，
提供视图生成、调用位置拦截及拦截命名空间配置。

```csharp
using SomeEngine.ECS.Entities;
using SomeEngine.Views;
using SomeEngine.Views.Compose;

public static partial class Screens
{
    [Composable]
    public static void Hud(Entity player, bool paused) { /* 声明子视图 */ }
}

// 根视图：Views.Spawn(world, Entity.Null, new Screens.Hud_View(player, false));
// 子视图：在 Build 中直接调用 Screens.Hud(player, false)。
```

包含类型与各级外层类型使用 `partial`，生成的 `<方法名>_View` 位于原方法的包含类型中。
视图主构造函数保留参数名称、类型、可选值和泛型约束；同名重载通过参数类型签名后缀区分视图类型。
公开方法生成公开视图，私有和程序集内方法生成程序集内视图。
可拦截调用的包含类型与参数类型应能从 `SomeEngine.Views.Generated` 命名空间引用。

方法返回 `void` 或 `BuildTask`。生成的 `Build` 直接执行原方法，调用方的直接调用则声明视图。
相等参数保持已有内容，变化参数更新该节点并重建内容，构建错误使用共同的 `BuildError` 与开发检查入口。
消费程序集引用定义程序集已生成的视图类型。

`SEV005` 提示 partial 接缝，`SEV006` 检查 static，`SEV007` 检查视图结果类型，
`SEV008` 检查方法组、委托、取地址、生成代码调用与实际拦截结果。
Views 包的 buildTransitive props 提供拦截命名空间，`SEV001` 检查该配置。
