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

## Toggle、Slider 与用户 Knob（#44）

```csharp
public record struct Toggle(bool On) : IView, ISparseComponent;
public record struct Slider(float Value, float Minimum = 0, float Maximum = 1, float Step = 0)
    : IView, ISparseComponent;
public static class ValueFunctions
{
    public static bool Toggled(this Entity toggle);
    public static bool Edited(this Entity node, out float value);
    public static Entity ValueDraggable(Entity node, in ValueRange range, Vector2 direction, float distance);
}
```

`ToggleFunctions.Toggle(on)` 与 `SliderFunctions.Slider(value, minimum, maximum, step)`
是同名生成入口。参数由调用方提供，交互结果由调用方写回自己的数据。
`Toggled()` 沿 `Clicked()` 消费新确认，调用方据此切换布尔值；`Edited()` 消费最新的数值编辑。
同一帧的多次数值输入顺序应用，读取取得最终值；再次读取返回 false，out 值为当前参数。

Toggle 在自己的节点上声明 `Pressable` 与 `Focusable`，用 40 × 24 的圆角外框和
16 × 16 的圆形滑块显示布尔值。Slider 声明共享的 `ValueDraggable`，用 160 × 24 的
外框、4 单位高的轨道和 12 × 12 的滑块显示数值，水平全程拖动距离为 148 画布单位。
焦点边框由构建读取 `Focused` 后选择。输入数据、布局矩形与调用方数值分别由原来的
输入阶段、布局阶段与应用持有。

外部 [Knob](../../../tests/SomeEngine.Elements.Consumer.Tests/Rewrites/Knob.cs)
调用同一个 `ValueDraggable`，将增加方向设为向上，全程距离同为 148。
它用自己的 48 × 48 圆形底面和沿圆周移动的指示点表达数值，颜色和子结构由用户定义。
共享部件负责范围、步长、键盘步进和编辑消费，控件选择自己的方向、距离与呈现。
外部 [Toggle](../../../tests/SomeEngine.Elements.Consumer.Tests/Rewrites/Toggle.cs)
同样通过公开指针、焦点、布局和 Surface 接口组合，并与内置 Toggle 共用确认用例。

音量贯穿场景通过公开 ECS 查询的 `row.ReadWrite<AudioSettings>()` 取得 ref，游戏系统
修改 `Volume`，视图读取这一份组件后声明 Slider、Knob 和数值文本。控件的 `Edited()`
结果通过 `Scope.Write<AudioSettings>` 写回，依赖变化在同帧重建中更新其它控件。
调用方先把 Volume 格式化为显示字符串，再传给 `Text(string)`；相同显示结果沿视图参数
比较保留 Text、TextContent、排版与布局版本。
共享的范围和输入语义见 [[architecture/someui/Input-and-Behaviors|Input and Behaviors]]。

独立 SomeUI 的控件专用输入、预览与外观结构作为交互参考。本票依据 ADR 0011，
以公开行为、普通视图和调用方数据写回表达这些能力；`Step = 0` 表示连续拖动，
键盘采用范围的 5% 步进，符合本票确定的 Slider 默认参数。

## TextField（#46）

```csharp
public record struct TextField(string Text, string Placeholder = "") : IView, ISparseComponent;
// using static SomeEngine.Elements.TextFieldFunctions;
Entity field = TextField(name, "姓名");
if (field.Edited(out string text))
    name = text;
```

TextField 是单行输入视图。它在自己的节点上声明公开的 `TextEditing` 和
`SingleLineText`，其中 `TextEditing` 同时声明 `Focusable`；调用方沿既有
`Edited(out string text)` 消费输入结果并写回自己的数据。

视图采用 `BuildMode.Once`，用公开 `Memo<string>` 按 `Text` 字符串的变化同步
调用方参数，用公开 `Scope.Compute` 提交文字变化。普通子视图读取编辑、焦点与
占位参数后重建外观。首次参数使光标与选区锚点位于
文字末尾；调用方传回当前编辑文字时保留光标、选区和输入法组合。传入其它文字
时，编辑内容替换为该参数，两端移到末尾，组合显示结束。参数同步保留编辑计数，
输入阶段继续负责递增计数。父视图重跑和占位文字更新沿字符串比较保留当前编辑。

外观通过公开的 Text、Surface、Absolute 与 ScrollView 组合：160 单位宽的圆角
底面带 8 单位内边距，字体、字号、行高和文字颜色从最近的 `TextTheme` 读取。
空文字时显示半透明占位文字。已聚焦的视图显示 1 单位宽的光标，选区显示蓝色
半透明底面。输入法候选替换选区的呈现内容，光标跟随 `CompositionCaret`。
文字按自然宽度排版，长文字沿 ScrollView 的公开滚动与裁剪规则显示。
几何按同一字体的字形簇 advance 计算，光标与选区随文字、组合和主题更新。

`Text` 与 `Placeholder` 使用单行字符串。参数使用错误沿 Core `DevelopmentCheck`
报告；文本输入中的 CR 与 LF 由 `SingleLineText` 的编辑规则过滤。
编辑、组合及计数语义见 [[architecture/someui/Input-and-Behaviors|Input and Behaviors]]。

独立 SomeUI 的 TextInput 提供了占位文字、焦点和光标的交互参考。本票依据
ADR 0011，以公开行为、普通视图和修饰组合表达这些能力；已提交文字、选区与
组合文字由同一份 `EditableText` 持有，应用通过编辑结果回传自己的参数。

外部 [TextField](../../../tests/SomeEngine.Elements.Consumer.Tests/Rewrites/TextField.cs)
在消费者程序集内通过相同公开部件重写，内置版本与重写版本共用
[TextFieldChecks](../../../tests/SomeEngine.Elements.Consumer.Tests/TextFieldChecks.cs)。

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
