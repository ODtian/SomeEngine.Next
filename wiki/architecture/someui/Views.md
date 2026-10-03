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

## Relations
[[architecture/someui/Build-Modes|Build Modes]] · [[architecture/someui/Child-Source|Child Source]] · [[architecture/someui/Node-Identity|Node Identity]] · [[architecture/someui/Build-Scope|Build Scope]]
