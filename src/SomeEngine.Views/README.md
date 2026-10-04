# SomeEngine.Views

视图是实现 `IView` 的 struct，参数作为 ECS 组件保存在节点实体上，`Build` 声明内容。
`Views.Spawn` 创建根，`ViewRebuildSystem` 完成首次构建及参数、读取依赖变化后的重跑。

`Scope.Compute(Action)` 在所属视图登记并立即执行无节点计算，依赖变化后单独重跑。
`Scope.Memo<T>(Func<T>)` 返回版本单元 `Memo<T>`，读取 `Value` 当场复核并重算过期值，
相等结果保留已有版本。计算随创建者重跑与所属视图释放，规则见
[Computations](../../wiki/architecture/someui/Computations.md)。

`Scope.Launch(key, run)` 声明随 key 变化重启的异步子任务。
`Scope.Resource(source, fetch)` 返回 `Resource<T>`，请求期间保留旧值，读取
`Value`、`Loading` 与 `Error` 登记依赖。两种原语共用视图重建队列和取消清理，
生成器提供调用位置身份；`callKey` 重载支持显式身份，`Scope.Key` 支持列表分组。
`Once` 视图通过 `Compute` 读取来源并重新声明资源，规则见
[Async Build](../../wiki/architecture/someui/Async-Build.md)。

包附带 `SomeEngine.Views.Generators` 分析器和 buildTransitive props。
生成器为带主构造函数的视图生成同名静态函数，每个视图对应一个函数类。

```csharp
global using static Inventory.CardFunctions;
using SomeEngine.Views;

namespace Inventory;

public readonly partial record struct Card(string Title) : IView
{
    public async BuildTask Build() { }
}
```

构建中使用 `Card("背包")` 或 `Scope.View(new Card("背包"))`，两种写法都返回 `Entity`。
编译期拦截器将文件、行和列生成的位置常量传入 `Scope.View(in T, ulong)`；
分支内的不同调用与同一行的不同列各有身份，循环内相同位置由出现次数区分。
列表项通过 `using (Scope.Key(id))` 组合各项内的调用位置。

`partial` 视图省略存储接口时，生成器补充 `ISparseComponent` 并给出 `SEV002` 信息提示。
视图可以显式选择 `IComponent`（表格）或 `ISparseComponent`（稀疏存储）。
嵌套视图的函数类位于相同外层类型，外层类型声明为 `partial`。
同名函数保持构造参数、可选值、泛型约束和传参方式；
普通 struct 自行实现 `IEquatable<T>`，record struct 自带参数比较。

包的 props 自动将 `SomeEngine.Views.Generated` 加入 `InterceptorsNamespaces`。
`SEV001` 检查项目配置，`SEV004` 检查实际拦截结果；方法组或委托形式可改为源码直接调用，
需要显式身份的调用可以使用双参数 `Scope.View`。
`IView.Build` 的 CS1998 由 suppressor 屏蔽，辅助异步方法保留编译器警告。

界面节点使用 ECS 序列化注册表保存参数与有序层级：

```csharp
var registry = new SomeEngine.ECS.Serialization.SerializationRegistry();
ViewSerialization.Register(registry);
CardFunctions.RegisterSerialization(registry);
GroupFunctions.RegisterSerialization(registry);
```

生成器提供固定字段 codec，也可通过 `RegisterSerialization<CardCodec>(registry)`
接入应用 codec。泛型视图在注册时提供闭合类型自己的 `SerializationTypeKey`。
`ViewType` 的序列化身份映射到当前进程的类型编号，读回后下一帧重建所有视图。
整 World 保存沿用 ECS 的无托管引用数据边界；含引用参数通过组件或实体入口编码。
完整字段支持与存档规则见仓库 Wiki `architecture/someui/Assemblies`。

仓库验收入口：

```powershell
dotnet test tests/SomeEngine.Views.Generators.Tests/SomeEngine.Views.Generators.Tests.csproj
dotnet test tests/SomeEngine.Views.Tests/SomeEngine.Views.Tests.csproj
just views-aot win-x64
```
