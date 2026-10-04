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
- 修饰在计算之外调用由开发检查报告；游戏代码使用 `World.Read` 与 `World.Replace` 读写组件。

## 布局修饰实现（#24）

`SomeEngine.Layout.LayoutModifiers` 提供 `Padding(all)`、`Size(width, height)`、
`MinSize(width, height)`、`MaxSize(width, height)` 与 `Position(x, y)`。
前四者通过 `Scope.Write<Box>` 更新对应数据，`Position` 写入 `AbsolutePosition.Value`。
每次修饰都返回传入实体；同一计算中的多次修饰共用组件副本，成功结束时比较提交。
`Size` 的任一轴允许 NaN，以保留该轴的自动测量；零尺寸表示有效固定值。
非均匀内边距可通过公开的 `Scope.Write<Box>(entity).Padding` 写入 `Thickness`。
Box 的处理顺序和默认值见 [[architecture/someui/Layout|Layout]]。

## Surface 修饰（#28）

`SomeEngine.Elements.Surface` 是布局节点上的表格组件，颜色使用 Core 的线性 RGBA `Color`：

```csharp
public struct Surface : IComponent
{
    public Color Fill;
    public float CornerRadius;
    public Color BorderColor;
    public float BorderWidth;
}

public static class SurfaceModifiers
{
    public static Entity Background(this Entity entity, Color fill);
    public static Entity Corner(this Entity entity, float radius);
    public static Entity Border(this Entity entity, Color color, float width);
}
```

```csharp
Column().Background(fill).Corner(4).Border(borderColor, 2);
```

调用方在参与布局的节点上使用这些修饰。`Background` 更新填充色，`Corner` 更新圆角半径，
`Border` 同时更新描边颜色和宽度；每个修饰保留 Surface 的其余字段并返回原实体。
圆角与描边宽度采用布局单位。默认 Surface 的颜色为透明黑，圆角和描边宽度为零。

这些方法沿用 `Scope.Write<Surface>` 的组件副本，成功计算结束时比较提交。
重建写入相等的四个字段时，节点与类型的写版本均保留；值变化时按 ECS 写入规则推进版本。
调用所需的构建或计算作用域由 Core `DevelopmentCheck` 的开发构建检查报告。
数据写入方由 [[architecture/someui/Data-Writers|Data Writers]] 维护，
公开行为与用户等价修饰的验收见 [[architecture/someui/Testing|Testing]]。

## Flex 修饰（#25）

`SomeEngine.Layout.FlexModifiers` 提供以下 `Entity` 扩展方法，每次返回传入实体：

| 修饰 | 调用方写入的数据 |
| --- | --- |
| `Grow(value = 1)` | `FlexItem.Grow` |
| `Shrink(value = 1)` | `FlexItem.Shrink` |
| `Basis(value)` | `FlexItem.Basis` |
| `AlignSelf(value)` | `FlexItem.Self` |
| `Gap(value)` | `Flex.Gap` |
| `Align(main, cross = Align.Stretch)` | `Flex.MainAlign` 与 `Flex.CrossAlign` |

这些方法沿 `Scope.Write` 取得同一计算中的组件副本，计算成功后比较提交。
`Gap` 与 `Align` 用于 Flex 视图，开发构建通过 Core 的 `DevelopmentCheck` 检查入口。
默认值、伸缩分配、各轴支持的对齐与行间距规则见 [[architecture/someui/Layout|Layout]]。

## Grid 修饰（#26）

`SomeEngine.Layout.GridModifiers.Cell(row, column, rowSpan = 1, columnSpan = 1)`
通过 `Scope.Write<GridCell>` 写入四项单元格数据，并返回传入实体。
默认值、显式行列定义、跨度尺寸与测量规则见 [[architecture/someui/Layout|Layout]]。

## Relations
[[architecture/someui/Data-Writers|Data Writers]] · [[architecture/someui/Layout|Layout]]
