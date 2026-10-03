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
- `IView` 与存储方式无关。每种视图自己实现 `IComponent` 或 `ISparseComponent`，按 [[architecture/someui/Component-Storage|Component Storage]] 选择。
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

## Relations
[[architecture/someui/Build-Modes|Build Modes]] · [[architecture/someui/Child-Source|Child Source]] · [[architecture/someui/Node-Identity|Node Identity]] · [[architecture/someui/Build-Scope|Build Scope]]
