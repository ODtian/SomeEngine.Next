# Modifiers

## Definition
修饰是写在节点上的数据，以 `Entity` 的扩展方法提供，返回同一个实体以便连续调用。

```csharp
public static Entity Padding(this Entity element, float value)
{
    Scope.Write<Box>(element).Padding = new Thickness(value, value, value, value);
    return element;
}

using (Column().Padding(16).Background(fill).Content()) { ... }
```

## Invariants
- 所有构建函数返回 `Entity`；只有 `.Content()` 返回用于 `using` 的 `ParentScope`。
- 修饰由调用方的作用域写入，按计算结束时比较提交；视图不给自己写修饰数据，默认值放在自身参数中。
- 修饰不是视图，不创建实体，也不参与子视图的参数比较。
- 影响布局的特殊包装（宽高比、偏移等）写成只有一个子节点的布局视图，不做修饰链。
- 用户的修饰与内置修饰写法相同。
- 修饰在计算之外调用由开发检查报告；游戏代码直接使用 `World.Write`。

## 布局修饰实现（#24）

`SomeEngine.Layout.LayoutModifiers` 提供 `Padding(all)`、`Size(width, height)`、
`MinSize(width, height)`、`MaxSize(width, height)` 与 `Position(x, y)`。
前四者通过 `Scope.Write<Box>` 更新对应数据，`Position` 写入 `AbsolutePosition.Value`。
每次修饰都返回传入实体；同一计算中的多次修饰共用组件副本，成功结束时比较提交。
`Size` 的任一轴允许 NaN，以保留该轴的自动测量；零尺寸表示有效固定值。
非均匀内边距可通过公开的 `Scope.Write<Box>(entity).Padding` 写入 `Thickness`。
Box 的处理顺序和默认值见 [[architecture/someui/Layout|Layout]]。

## Relations
[[architecture/someui/Data-Writers|Data Writers]] · [[architecture/someui/Layout|Layout]]
