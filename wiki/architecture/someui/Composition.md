# Composition

## Definition
库与用户代码对等：内置元素、控件、布局和行为都是用公开部件组合出来的普通代码。用户需要不同的东西时，用同样的部件组合出自己的版本。

```csharp
public record struct IconButton(string Glyph, string Label) : IView, ISparseComponent
{
    public BuildTask Build()
    {
        PressableFunctions.Pressable(Scope.Entity);
        ButtonTheme theme = Scope.ReadUp<ButtonTheme>();
        Hover hover = Scope.Read<Hover>(Scope.Entity);
        Color fill = hover.Pressed ? theme.PressedFill : hover.Hovered ? theme.HoverFill : theme.Fill;
        Entity row = Row(4).Background(fill).Corner(theme.CornerRadius);
        Scope.Write<Box>(row).Padding = theme.Padding;
        using (row.Content())
        {
            Text(Glyph).TextColor(theme.TextColor);
            Text(Label).TextColor(theme.TextColor);
        }
        return default;
    }
}

using (Dialog().Content())
{
    DialogTitle("设置");
    VolumeSlider(settings);
    using (DialogActions().Content()) { Button("确定"); IconButton("★", "收藏"); }
}
```

## Invariants
- 行为与外观分开：`Pressable`、`Draggable`、`Focusable`、`Scrollable`、`TextEditing`、`Selectable` 是可挂到任意节点的行为部件。
- 内置控件是行为部件、元素与布局的薄组合；复合控件拆成由调用方拼装的复合部件，部件之间通过 `ReadUp<T>()` 共享数据。
- 组件需要决定内容放置时机、次数或参数时，接收内容函数参数（推荐 `static` lambda 加状态参数）。
- 共享的是数据：主题等环境数据由 `entity.Provide(value)` 写在祖先上，通过 `ReadUp<T>()` 读取；用户可以定义自己的主题类型。
- 内置控件的函数按控件放在各自的静态类中，用户可以只导入需要的部分，或在自己的命名空间提供同名组件。

## Non-goals
- 替换（`Replace`/`Map`）、覆盖层（`Override`）、具名内容槽。

## 环境数据入口（#13）

```csharp
public static class EnvironmentExtensions
{
    public static Entity Provide<T>(this Entity node, in T value) where T : struct;
}

// 当前构建的调用方在节点上提供主题数据。
Scope.Entity.Provide(new Theme(Accent: 0xff336699));
// 后代构建读取最近节点的数据，Theme 为用户定义的 ECS 组件。
ref readonly Theme theme = ref Scope.ReadUp<Theme>();
```

`Provide` 将值写入当前构建的 `Scope.Write<T>(node)` 临时副本，并返回原节点以继续链式调用。
成功构建结束时按 `Write` 的比较提交规则保存组件并推进写版本；表格与 sparse 使用相同入口。
同一节点的同一环境组件由调用方写入，最近提供者的整个组件作为读取结果。
读取依赖和父链变化处理见 [[architecture/someui/Dependencies|Dependencies]]。

公开验收：[EnvironmentTests](../../../tests/SomeEngine.Views.Tests/EnvironmentTests.cs)。

## Button 与主题（#43）

公开类型位于 `SomeEngine.Elements`：

```csharp
public record struct Button(string Label) : IView, ISparseComponent;
public struct ButtonTheme : IComponent
{
    public Color Fill { readonly get; set; }
    public Color HoverFill { readonly get; set; }
    public Color PressedFill { readonly get; set; }
    public Color TextColor { readonly get; set; }
    public Thickness Padding { readonly get; set; }
    public float CornerRadius { readonly get; set; }
}
// using static SomeEngine.Elements.ButtonFunctions;
if (Button("确定").Clicked()) { /* 调用方处理新点击 */ }
```

`ButtonTheme` 的六个成员采用可写属性，沿用 `TextStyle` 和 `Box` 的默认值表达。
`default(ButtonTheme)`、`new ButtonTheme()` 和省略的属性具有下表默认值；
显式设置的透明颜色、零圆角与零内边距作为用户数据保留。颜色使用线性 RGBA，尺寸使用布局单位。

| 属性 | 默认值 |
|---|---|
| `Fill` | `(0.2, 0.2, 0.2, 1)` |
| `HoverFill` | `(0.3, 0.3, 0.3, 1)` |
| `PressedFill` | `(0.1, 0.1, 0.1, 1)` |
| `TextColor` | `(1, 1, 1, 1)` |
| `Padding` | `(8, 8, 8, 8)`，顺序为左、上、右、下 |
| `CornerRadius` | `4` |

Button 在自己的节点上调用 `Pressable` 声明输入数据，读取 `Hover` 和
`Scope.ReadUp<ButtonTheme>()`，并选择按下色、悬停色或普通色，按下色优先。
子 Row 承载 `Surface` 与 `Box.Padding`，子 Text 承载标签及显式 `TextColor`；
字体、字号与行高继续由最近的 `TextTheme` 提供。调用方通过 `ButtonFunctions.Button(Label)`
声明视图，在返回节点上调用 `Clicked()` 消费点击。输入状态与布局的写入方见
[[architecture/someui/Input-and-Behaviors|Input and Behaviors]] 与
[[architecture/someui/Data-Writers|Data Writers]]。

```csharp
using (Column(6).Provide(new ButtonTheme
{
    Fill = new Color(0.15f, 0.3f, 0.6f),
    Padding = new Thickness(12, 6, 12, 6),
}).Content())
{
    Button("确定");
    Button("取消");
}
```

最近提供者的整个 `ButtonTheme` 供子树读取，提供者更新后同帧刷新按钮外观。
中间节点提供的主题使用该主题自身的属性默认值。Button 自身读取主题与悬停数据，
调用方通过 `Clicked()` 读取点击数据，各数据沿对应的依赖登记与提交规则维护。

外部工程的 [Button](../../../tests/SomeEngine.Elements.Consumer.Tests/Rewrites/Button.cs)
使用同样的公开部件重写；[IconButton](../../../tests/SomeEngine.Elements.Consumer.Tests/Rewrites/IconButton.cs)
把图标字形和标签作为两个 Text 放入间距为 4 的 Row。二者与内置 Button 共用公开行为用例。
示例中的 `Glyph` 是文本字形，调用方通过 `TextTheme` 选择字体。

独立 SomeUI 参考实现采用按钮专用挂载、输入与外观数据；本票依据 ADR 0011，
采用公开视图、行为与修饰组合，输入数据由命中系统持有，主题数据由提供者持有。
这一取舍把内置与用户控件放在相同的公开接口上。

## Evidence
对等由程序集边界保证，见 [[architecture/someui/Assemblies|Assemblies]]；外部重写测试见 [[architecture/someui/Testing|Testing]]。

`Group` 是已实现的透明容器，用户视图也可声明 `ChildSource.Caller`。
当前底层组合入口是 `using (Scope.View(new Group()).Content()) { Scope.View(...); }`；
内容作用域和公开行为证据见 [[architecture/someui/Child-Source|Child Source]]。

Compose 组件通过 `[Composable] Action<T>` 等内容参数选择内容的放置时机和数据。
内容调用形成独立子视图，委托按引用比较，推荐 `static` lambda 加显式状态参数。
API、节点身份、跨程序集转交及公开行为验收见
[[architecture/someui/Compose-Frontend|Compose Frontend]] 的内容函数参数章节。

## Relations
ADR 0011 · [[architecture/someui/Child-Source|Child Source]] · [[architecture/someui/Input-and-Behaviors|Input and Behaviors]]
