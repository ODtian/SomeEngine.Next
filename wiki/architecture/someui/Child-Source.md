# Child Source

## Definition
子节点来源是视图类型声明的子节点声明者，表达数据归属，不表达内部结构。

```csharp
public enum ChildSource { Build, Caller }
```

- `Build`：子节点由视图自己的 `Build` 声明，调用方不能打开它。
- `Caller`：视图是容器（如 `Flex`），`Build` 只写自身数据，子节点由调用方通过 `entity.Content()` 打开后声明。

## Invariants
- 节点实体本身归创建它的构建；子节点归打开它的 `.Content()` 块。
- `.Content()` 块结束时，块内声明的就是该节点的全部子节点，未声明的旧子节点被释放。
- 一个视图不会同时属于两种来源；不存在具名内容区或槽。
- 子节点声明由同一视图持有；每次该视图构建为父节点声明一次。来源使用错误、不同视图同时声明同一父节点，以及单次构建中的重复声明通过开发检查报告。

## Non-goals
- 具名内容区（`Content<TArea>`）与区域标记类型。复合控件改用复合部件，见 [[architecture/someui/Composition|Composition]]。

## 子节点来源实现（#17）

`SomeEngine.Views` 提供容器内容入口与透明容器：

```csharp
public static class ContentExtensions
{
    public static ParentScope Content(this Entity node);
}

public readonly ref struct ParentScope
{
    public Entity Entity { get; }
    public void Dispose();
}

public record struct Group : IView, IComponent
{
    public static ChildSource Children => ChildSource.Caller;
    public BuildTask Build() => default;
}
```

底层声明写法为 `using (Scope.View(new Group()).Content()) { Scope.View(...); }`。
`Content()` 在活动构建中打开目标容器，`ParentScope.Entity` 返回这个容器实体。
块内的 `Scope.View` 将目标容器作为父节点，按
[[architecture/someui/Node-Identity|Node Identity]] 的 key 加出现次数匹配，
复用或新建节点并维护声明顺序。块结束时完成此次全部子节点声明，
删除剩余旧节点及其整个子树；空块对应空的子节点序列。

`ChildSource.Build` 的构建在成功完成时执行同一子节点匹配收尾。
`ChildSource.Caller` 的构建只更新自身数据，已有子树由调用方的内容作用域维护。
因此容器因参数变化单独重跑时保留内容；调用方重跑时，
参数相等的容器保持自身参数和输出的写版本，内容作用域继续更新其子节点。

内容作用域继承当前构建的 World、构建实体和读取登记状态；
`Scope.Entity` 继续表示正在构建的视图，`Scope.Read<T>` 的依赖归该视图的计算。
整个构建中的内容块共用该视图的依赖缓冲，`Once` 构建沿仅取值状态读取。
每个内容作用域从自身父节点的 key 上下文开始，
嵌套的内容块结束后恢复外层父节点、出现次数和 key 前缀。
使用 `ParentScope.Entity` 可以向内容容器写入数据。

开发构建通过 Core `DevelopmentCheck` 报告子节点来源的使用错误：
对 `ChildSource.Build` 调用 `Content()`、在 `ChildSource.Caller.Build` 中直接声明子视图，
以及同次重建阶段中不同视图声明同一父节点、单次视图构建重复声明该父节点。
声明记录覆盖 `ViewRebuildSystem` 的整个阶段及其中的嵌套构建，以视图实体与该次构建作用域记录声明。
读取依赖在构建后变化时，同一视图的下一次构建可以再次声明自己的子节点。
记录在阶段退出时恢复；内容作用域通过 `finally` 恢复外层上下文。
来源检查、阶段声明记录和重复 key 的局部状态检查由 `DEBUG` 条件编译控制。

公开行为测试为 `tests/SomeEngine.Views.Tests/ChildSourceTests.cs`，
覆盖两种组件存储的容器数据与内容更新、子树释放、keyed 重排、
嵌套作用域、读取依赖与开发检查。`FirstBuildChecks.cs` 同时在 ECS NativeAOT 冒烟中
验证调用方内容的声明、保留、更新与释放。

## Relations
[[architecture/someui/Views|Views]] · [[architecture/someui/Composition|Composition]]
