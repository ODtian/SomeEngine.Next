# Views

## Definition
视图是实现 `IView` 的 struct：字段是参数，`Build` 是构建方法。界面树上的每个节点都是一个视图实体，内置元素（`Text`、`Flex`、`Image`）、控件、用户视图和各前端产出的组件都是视图。

```csharp
public interface IView
{
    BuildTask Build();
    static virtual BuildMode Mode => BuildMode.Rerun;
    static virtual ChildSource Children => ChildSource.Build;
}

public record struct Text(string Value) : IView, IComponent
{
    public BuildTask Build() => default;
}
```

## Invariants
- 参数作为组件保存在节点实体上，并且就是系统消费的节点数据；不另存一份派生副本。
- `IView` 与存储方式无关。视图通过 `IComponent` 或 `ISparseComponent` 选择存储；生成器为省略这两种声明的 `partial` 视图补充 sparse 存储，并给出 info 级选择提示。选择依据见 [[architecture/someui/Component-Storage|Component Storage]]。
- `View<T>(in T)` 返回节点实体：新建或参数不相等时写入参数，并在 `Rerun` 模式下当场构建；参数相等时静默覆盖存储（不推进版本）并跳过。
- 源生成器为每个带主构造函数的视图生成同名函数，与 `View(new T(...))` 等价，返回 `Entity`。
- 构建入口和布局入口按视图类型存在函数指针表中，节点只保存公共组件 `ViewType`（类型编号）与 `ElementKey`；依赖记录按需添加。
- 修饰与普通函数不是视图：修饰是写在节点上的数据，普通函数在调用者的作用域中内联执行。

## 首次构建实现（#6）

`SomeEngine.Views` 提供 `IView`、`BuildMode`、`ChildSource`、`BuildTask`、
`ElementTree`、`ViewType`、`Views.Spawn`、`Scope.View` 和 `ViewRebuildSystem`。
程序集依赖 ECS、ECS.Systems、Job，并使用 Core 的 `DevelopmentCheck`。

```csharp
Entity root = Views.Spawn(world, Entity.Null, new Panel("Inventory"));
// 将 ViewRebuildSystem 加入 ImmediateSystemContext 的系统组，随后推进一帧。
```

- `Spawn<T>(World, Entity, in T)` 的约束为 `struct, IView, IEquatable<T>`。
  创建时写入实际的 `T` 参数组件与 `ViewType`，根视图的首次 `Build` 在重建阶段执行。
  `Entity.Null` 表示独立根；传入父实体时，根附加到该实体的 `ElementTree` 子节点末尾。
- `Scope.View<T>(in T)` 使用相同约束。首次创建的子节点附加到当前节点末尾，
  随后通过嵌套作用域完成首次构建；`Rerun` 与 `Once` 的首次内容均沿此路径生成。
  模式声明为后续参数传播与依赖重跑提供类型信息。
- 每个视图节点的 `ElementTree` 子序列使用 `ChildOrderPolicy.Ordered`，顺序来自声明。
- 类型编号由泛型静态缓存注册到托管函数指针表；相同视图类型在各 World 中共用编号。
  构建函数从当前 ECS 参数组件取值，因此首次帧之前对该组件的修改直接供构建使用。
- `BuildTask` 的当前同步完成行为与阶段范围见
  [[architecture/someui/Async-Build|Async Build]]。

测试入口为 `tests/SomeEngine.Views.Tests/FirstBuildTests.cs`。
共享公开行为检查 `FirstBuildChecks.cs` 同时编入正式 ECS NativeAOT 冒烟工程，
通过 `just ecs-aot win-x64` 驱动含托管参数、两种存储和三层视图的同步构建。

## 参数更新实现（#7）

`Scope.View<T>(in T)` 与显式 key 重载复用同一父节点下对应身份的子实体，并使用
`IEquatable<T>.Equals` 比较旧参数与声明的参数。
参数比较、构建模式与外部 ECS 写入的更新规则由
[[architecture/someui/Build-Modes|Build Modes]] 维护。

公开行为测试 `ParameterBuildTests.cs` 验证两种存储的版本、输出与嵌套节点复用。
`FirstBuildChecks.cs` 同时在 ECS NativeAOT 冒烟工程验证相等参数保持、
稀疏参数向表格子视图传播与 `Once` 内容保持。

## 节点身份实现（#8）

`ElementKey` 保存 key 和从 0 开始的出现次数，`Scope.View<T>(in T, ulong)`
和 `Scope.Key(ulong)` 提供显式身份。匹配、重排、子树释放、类型替换、局部状态标记
及开发检查由 [[architecture/someui/Node-Identity|Node Identity]] 维护。
公开行为测试 `NodeIdentityTests.cs` 覆盖列表和默认 key 声明，
`FirstBuildChecks.cs` 同时验证 NativeAOT 下的 keyed 节点生命周期。

## 同名函数实现（#19）

`SomeEngine.Views.Generators` 为带主构造函数的 `IView` struct 生成
`<视图名>Functions` 静态 partial 类，保留构造参数的名称、类型、默认值、传参方式
及泛型约束。函数与视图使用同一命名空间；嵌套视图的函数类位于相同的外层类型内。
每个视图对应自己的函数类，调用方可以按视图选择 `global using static`。

```csharp
global using static Inventory.CardFunctions;
using SomeEngine.Views;

namespace Inventory;

public readonly partial record struct Card(string Title) : IView
{
    public async BuildTask Build() { }
}
// 在构建中使用 Card("背包")，返回 Entity。
```

- 上例由生成器补充 `ISparseComponent`，并以 `SEV002`（info）提示明确选择存储。
  显式实现 `IComponent` 或 `ISparseComponent` 的视图沿其声明存储。
  为补充接口，省略存储声明的视图需为 `partial`（`SEV003`）；
  嵌套视图的外层类型需为 `partial`（`SEV005`）。
- `Scope.View` 的 `struct, IView, IEquatable<T>` 约束同样适用于同名函数；
  `record struct` 提供参数相等比较，普通 struct 可以自己实现 `IEquatable<T>`。
- 函数生成 `Declare<视图名>(ulong callKey, ...)` 入口供拦截器传入调用位置常量，
  通过公开的 `Scope.View(in T, ulong)` 声明节点。这样私有嵌套视图的实例化留在
  其外层类型中；能够对外表示的构造参数允许同名函数参与调用位置拦截。
  带私有构造参数类型的函数类保留在外层类型内，调用方通过 `Scope.View(new ...)`
  声明这类视图。编译期接入规则见 [[architecture/someui/Checks|Checks]]。
- 普通函数、lambda 与生成的同名函数体中的节点声明同样获得位置常量。
  位置与列表分组规则由 [[architecture/someui/Node-Identity|Node Identity]] 维护。
- `BuildWarningSuppressor` 对实际实现 `IView.Build` 的方法屏蔽 CS1998，
  包含显式接口实现；辅助异步方法继续使用编译器自己的警告规则。

`SomeEngine.Views` 包内提供分析器 DLL 与 `buildTransitive/SomeEngine.Views.props`；
仓库的 `Directory.Build.props` 启用 `SomeEngine.Views.Generated`，
`Directory.Build.targets` 为直接引用 Views 的项目接入生成器。
公开行为测试入口为 `tests/SomeEngine.Views.Generators.Tests`。
`tools/SomeEngine.Views.AotSmoke` 通过 `win-x64` NativeAOT 发布并执行相同公开入口。

## 调用方内容实现（#17）

`ContentExtensions.Content(this Entity)` 返回用于 `using` 的 `ParentScope`，
`Group` 是公开的透明容器视图。两种子节点来源的声明、收尾与重跑规则见
[[architecture/someui/Child-Source|Child Source]]，公开行为测试为 `ChildSourceTests.cs`。

## 测量类型登记（#24）

`IView.Measure` 是泛型类型登记使用的默认测量接缝，`ILayout` 重申为必需实现。
布局视图以公开 `Measure` 或显式 `IView.Measure` 提供测量方法；
类型表记录它的测量函数指针。协议接口、单遍测量、透明组合与程序集拆分见
[[architecture/someui/Layout|Layout]]。

## Relations
[[architecture/someui/Build-Modes|Build Modes]] · [[architecture/someui/Child-Source|Child Source]] · [[architecture/someui/Node-Identity|Node Identity]] · [[architecture/someui/Build-Scope|Build Scope]]
