# Input and Behaviors

## Definition
输入以数据表达：命中测试与路由系统写入节点上的指针数据，行为部件声明自己需要的数据，读取这些数据的计算因版本变化而执行。

## Invariants
- 点击是单调递增的计数 `Clicks`，与悬停、按下状态 `Hover` 分开存放，避免悬停变化使读取点击的计算重跑。`entity.Clicked()` 比较计数与该节点记录的已读值。
- 冒泡：路由系统沿 `ElementTree` 把点击计数写到目标及同一画布内带 `Pressable` 的祖先，处理带 `StopsPointer` 标记的节点后停止。
- 悬停：`Hover.Hovered` 表示命中目标；`Hover.Within` 表示指针命中该节点或同一画布内的后代。
- 输入归属：命中测试在游戏输入之前运行，结果写入单例 `InputCapture`，游戏系统据此决定是否处理本次输入。
- 焦点：全局一个实体带 `Focused`；`Focusable` 为标记；键盘与手柄导航由可通过系统调度替换的 `FocusNavigationSystem` 根据布局结果计算。
- 文本输入：平台通过 `Keyboard.Text` 与 `Keyboard.Composition` 将字符和组合文本追加到 World 的 `FocusInput` 缓冲，文本编辑系统按事件所在的焦点修改节点的 `EditableText`。
- 行为部件（`Pressable`、`Draggable`、`Focusable`、`Scrollable`、`TextEditing`、`Selectable`）用 `Ensure<T>` 声明数据，可挂到任意节点。

## 指针输入与冒泡实现（#37、#38）

以下公开类型位于 `SomeEngine.Elements`：

```csharp
public static class Pointer
{
    public static void Move(World world, Entity canvas, Vector2 position);
    public static void Press(World world, Entity canvas, PointerButton button);
    public static void Release(World world, Entity canvas, PointerButton button);
    public static void Wheel(World world, Entity canvas, Vector2 delta);
}
public enum PointerButton : byte { Primary, Secondary, Middle }
public struct Hover : IComponent { public bool Hovered; public bool Pressed; public bool Within; }
public struct Clicks : IComponent { public uint Count; }
public struct ClickSeen : IComponent { public uint Count; }
public struct Pressable : IComponent { }
public struct StopsPointer : IComponent { }
public struct InputCapture : IComponent { public bool Pointer; public bool Keyboard; }
public static class PressableFunctions
{
    public static Entity Pressable(Entity node);
    public static bool Clicked(this Entity node);
}
public sealed class HitTestSystem : ISystem<ImmediateSystemContext> { /* ... */ }
```

应用在 World 的输入阶段调用 `Pointer`。`Move` 的位置使用与 `LayoutRect` 相同的画布单位，
应用把目标像素坐标除以 `Canvas.Scale` 后传入；世界空间或纹理画布的应用也先转换到画布坐标。
`Press`、`Release` 与 `Wheel` 使用这个画布最近一次 `Move` 提供的位置。
每个画布持有自己的事件缓冲与持续指针数据，事件按注入顺序领取，下一次命中阶段处理。
嵌套画布分别处理自己的输入及子树。使用有效画布、有限位置和滚轮增量、已定义按钮的要求
通过 Core `DevelopmentCheck` 检查。

`HitTestSystem` 注册在游戏输入系统之前，随后运行 `ViewRebuildSystem` 与 `LayoutSystem`。
系统读取前一帧布局完成后留下的 `LayoutRect`，按 `ElementTree` 绘制树序的逆序查找。
矩形包含左、上边界，右、下边界归相邻区域。具有布局矩形的 `Pressable` 节点以自己的矩形
参与命中。布局透明的 `Pressable` 组合视图以子内容的布局矩形提供命中区域；后代声明自己的
`Pressable` 时使用该后代作为目标。每次命中的目标都是声明了 `Pressable` 的节点。
这样组合视图可以在自己的实体上声明行为，布局结果继续由布局系统持有和写入。

各按钮分别配对按下与释放。同一个按钮在已有按下期间保留原目标；在原目标上释放时，
保持点击手势时，本次点击从该目标沿 `ElementTree` 向画布根路由，每个带 `Pressable` 的节点的
`Clicks.Count` 递增一次。路由包含画布根。节点带 `StopsPointer` 时，先处理该节点的
点击计数，再结束冒泡；仅带停止标记的中间节点同样结束冒泡。
重复释放保持当前计数。嵌套画布的点击在自身画布根结束。
`Hover.Hovered` 表示当前命中目标，`Hover.Pressed` 表示该节点持有至少一个按钮的按下，
移动到区域外时继续保留按下，直到释放。目标退出当前画布的可交互树后，按下目标清空。
`Hover.Within` 在命中目标及同一画布内声明了 `Hover` 的祖先上为 true，其余节点为 false。
因此祖先可以读取子树悬停，`Hovered` 与 `Pressed` 分别继续表达命中目标与按下目标。
`Within` 随当前命中子树变化，`StopsPointer` 约束点击计数的传播。
下一帧按新布局重新判断悬停与 `Within`，因此静止指针也能观察布局移动。
滚轮事件经过相同命中与输入捕获阶段，当前指针路径消费它的事件缓冲。

`PressableFunctions.Pressable(node)` 用 `Scope.Ensure` 声明 `Hover`、`Clicks` 与标记，
并返回原节点。调用方可以用相同公开组件和作用域函数实现自己的版本。
祖先只需观察子树悬停时，通过 `Scope.Ensure<Hover>(node)` 声明数据。
调用方通过 `Scope.Ensure<StopsPointer>(node)` 声明点击冒泡边界；公开 ECS 的标记增删
在下一次命中阶段生效。点击计数与所有 `Hover` 字段继续由 `HitTestSystem` 写入。
`node.Clicked()` 通过 `Scope.Read<Clicks>` 登记点击计数依赖，通过 `Scope.Write<ClickSeen>`
比较和保存该节点已经读过的计数；首次声明由正常写入提交完成。同一计算中的多次读取
使用同一份写入副本，成功计算结束时提交。返回值表示存在新点击，一次读取消费当时的全部
新计数；同帧多次完整点击仍分别递增 `Clicks.Count`。计数采用 `uint` 递增，已读值以 `!=`
比较以支持回绕。`Hover` 的变化只使读取 `Hover` 的计算失效。

每个 World 使用一个 `InputCapture` 实体。应用可以先建立这份单例，命中系统创建时取得它，
或在首次创建时建立默认单例。游戏输入通过公开查询读取该组件。
指针悬停在可交互目标上、仍持有界面按下，或本帧已处理指向界面或释放界面按下的输入时，
`InputCapture.Pointer` 为 true。空白位置且已经结束界面按下的下一帧为 false。
多个画布的捕获结果合并到同一份单例；命中系统在修改 `Pointer` 时保留 `Keyboard`。

实现：[Pointer](../../../src/SomeEngine.Elements/Pointer.cs)、
[HitTestSystem](../../../src/SomeEngine.Elements/HitTestSystem.cs)、
[公开指针数据](../../../src/SomeEngine.Elements/PointerComponents.cs)、
[PressableFunctions](../../../src/SomeEngine.Elements/PressableFunctions.cs)。
验收：[PointerTests](../../../tests/SomeEngine.Elements.Consumer.Tests/PointerTests.cs)、
[公开行为与 NativeAOT 共用检查](../../../tests/SomeEngine.Elements.Consumer.Tests/PointerChecks.cs)、
[冒泡与子树悬停共用检查](../../../tests/SomeEngine.Elements.Consumer.Tests/PointerRoutingChecks.cs)、
[用户重写](../../../tests/SomeEngine.Elements.Consumer.Tests/Rewrites/PressableFunctions.cs)。

## 焦点与键盘、手柄导航实现（#39）

以下公开类型位于 `SomeEngine.Elements`：

```csharp
public struct Focusable : IComponent { }
public struct Focused : ISparseComponent { }
public enum Direction : byte { Left, Right, Up, Down }
public enum Key : byte { Left, Right, Up, Down, Enter, Space, Home, End, Shift, Backspace, Delete }
public static class Keyboard
{
    public static void Key(World world, Key key, bool pressed);
}
public static class Gamepad
{
    public static void Navigate(World world, Direction direction);
    public static void Confirm(World world);
}
public static class FocusFunctions
{
    public static Entity Focusable(Entity node);
    public static bool Focused(this Entity node);
}
public enum FocusInputKind : byte { Key, Navigate, Confirm, Text, Composition }
public struct FocusInput : IBufferElement
{
    public FocusInputKind Kind;
    public Key Key;
    public bool Pressed;
    public Direction Direction;
    public string? Text;
    public int CompositionCaret;
}
public sealed class FocusNavigationSystem : ISystem<ImmediateSystemContext> { /* ... */ }
```

应用在输入阶段调用 `Keyboard` 和 `Gamepad`，事件按注入顺序追加到 World 单例
`InputCapture` 实体的 `FocusInput` 缓冲。首次输入或首次创建导航系统时建立默认单例和缓冲；
应用也可以事先创建自己的单例。导航系统在下一次输入阶段领取并清空事件。
`Key` 保存按下和释放两种变化；默认系统对每次 `pressed: true` 执行一次对应动作，
`pressed: false` 保持当前焦点与点击计数。平台的重复按键通过再次注入按下事件表达。
普通可聚焦节点的方向键执行导航，`Enter` 与 `Space` 执行确认；可编辑节点的键盘规则见下节。有效 World、已定义的键与方向，以及
每个 World 的单例数量要求通过 Core `DevelopmentCheck` 检查。

将 `FocusNavigationSystem` 与 `HitTestSystem` 注册在游戏输入之前，随后运行
`ViewRebuildSystem` 和 `LayoutSystem`。两个输入系统共享 `InputCapture`，分别写入
`Keyboard` 和 `Pointer`。默认导航系统使 `Keyboard` 表示当前 World 持有界面焦点。
游戏系统在自己的输入阶段通过公开查询读取这份数据。

默认导航使用上一帧完成的布局。在当前焦点所在的画布内，按候选矩形中心相对当前中心
的方向筛选：左右使用 X 的大小，上下使用 Y 的大小。候选中选择中心距离最近的节点，
等距时选择实体编号较小者；方向边界保持当前焦点。首次方向输入按中心 Y、中心 X、
实体编号的顺序选择左上方节点。每个 World 至多一个节点带 `Focused`，各 World 独立维护。
普通布局节点使用自己的 `LayoutRect`；布局透明的组合视图使用同一画布内子内容矩形的
包围矩形，焦点继续写在声明 `Focusable` 的组合节点上。

确认输入递增当前焦点节点的 `Clicks.Count`，读取与消费继续沿用上面的 `Clicked()`。
同帧的导航与确认严格按缓冲顺序执行，因此确认作用于它之前的导航结果。
`FocusFunctions.Focusable(node)` 用 `Scope.Ensure` 声明 `Focusable` 和 `Clicks`，
并返回原节点。`node.Focused()` 通过 `Scope.Has<Focused>` 读取 sparse 标记并登记存在性依赖，
取得与失去焦点都使调用方计算更新。

导航系统通过公开的 `World.EntityDestroying` 接入实体释放边界，在实体组件与父链接仍可读取
时检查 `Focused`，沿当时的 `ElementTree` 父链找到最近的 `Focusable` 祖先，并将标记转移到它。
父链的终点使焦点清空。视图重建释放和 `World.DestroyEntity`、
`Hierarchy<ElementTree>.DestroySubtree` 共用这一边界；子树按后序释放时，焦点逐层转移到存活
的可聚焦祖先。当前节点的 `Focusable` 被移除时，下一次导航阶段执行相同的祖先转移规则。
释放时同步更新 `InputCapture.Keyboard`，每帧直接读取 ECS 中的焦点与父链。

用户通过普通 `SystemGroup<ImmediateSystemContext>` 注册自己的输入系统，或禁用默认系统
并注册自定义系统。自定义系统从同一个公开 `FocusInput` 缓冲领取事件，通过公开 World API
写入 `Focused`、`Clicks` 与输入捕获，并实现自己的导航规则。

实现：[焦点与输入数据](../../../src/SomeEngine.Elements/FocusComponents.cs)、
[Keyboard](../../../src/SomeEngine.Elements/Keyboard.cs)、[Gamepad](../../../src/SomeEngine.Elements/Gamepad.cs)、
[FocusFunctions](../../../src/SomeEngine.Elements/FocusFunctions.cs)、
[FocusNavigationSystem](../../../src/SomeEngine.Elements/FocusNavigationSystem.cs)。
公开验收：[FocusTests](../../../tests/SomeEngine.Elements.Consumer.Tests/FocusTests.cs)，
共享检查与外部重写位于同一消费者工程，覆盖网格导航、确认消费、焦点读取依赖、
祖先转移、清空、父链移动、布局透明组合、最近中心与上一帧布局、画布内导航、输入顺序、
捕获字段、开发检查和调度中的自定义导航。同一组行为检查接入 `just views-aot win-x64` 的原生验收。

## 文本编辑与输入法组合实现（#41）

以下公开类型位于 `SomeEngine.Elements`：

```csharp
public struct EditableText : IComponent
{
    public string Text;
    public int Caret;
    public int SelectionStart;
    public string Composition;
    public int CompositionCaret;
    public uint Edits;
}
public struct EditSeen : IComponent { public uint Count; }
public static class Keyboard
{
    public static void Text(World world, string text);
    public static void Composition(World world, string text, int caret);
}
public static class TextEditingFunctions
{
    public static Entity TextEditing(Entity node);
    public static bool Edited(this Entity node, out string text);
}
public static class TextEditingSystem
{
    public static bool HandleInput(World world, Entity node, in FocusInput input, bool selecting = false);
    public static void CancelComposition(World world, Entity node);
}
```

`TextEditing(node)` 通过公开 `Scope.Ensure` 声明 `EditableText`、`Focusable` 和 `Clicks`，
返回原节点并保留现有值。`EditableText` 是节点文本编辑数据的主要持有者，输入阶段由
`TextEditingSystem` 写入；视图读取组件并组合自己的文字、选区和光标外观。首次声明的
字符串字段按空字符串解释，光标与选区起点为 0；应用可以在声明前提供有效初值。

`Caret` 是活动端，`SelectionStart` 是选区锚点，两者相等时选区折叠。它们使用 .NET
字符串的 UTF-16 偏移，并落在完整文本元素边界上；emoji、代理对与组合字符一起移动和删除。
字符输入替换两个端点之间的文字，将两端移到插入文字后的文本元素边界。插入连接字符
使两侧合成一个文本元素时，光标移到该元素末尾。空字符输入保留现有文本、选区与组合显示。

可编辑节点的 Left / Right 跨一个文本元素移动；已有选区在释放 Shift 后分别向起点或终点
折叠。Home / End 移到整个字符串的起点或终点。Up / Down 在显式换行之间按当前文本元素
列移动，较短行使用行尾；LF 与 CRLF 都作为换行。首末行的上下边界保持当前位置。
按住 Shift 时，上述移动保留锚点并扩展选区，释放时结束扩展；Shift 状态由默认输入系统
按键事件维护。Backspace / Delete 优先移除选区，选区折叠时分别删除前后一个完整文本元素。
文本首尾的删除保持原值。按下事件各执行一次，包括平台重复按键；释放事件保留文本状态。
编辑器领取四个方向键、Home、End、删除键、Enter 与 Space。平台通过 `Keyboard.Text`
提供空格、换行与确认字符，手柄方向和确认继续沿焦点导航与 `Clicks` 处理。

`Keyboard.Composition(world, text, caret)` 写入独立的 `Composition` 与 `CompositionCaret`。
组合光标是平台给出的组合字符串 UTF-16 偏移，范围包含字符串首尾。选区和已提交 `Text`
保持原值，调用方显示时以组合文字替换该选区的呈现内容。重复组合输入更新候选和组合光标，
可通过正常组件读取依赖刷新外观。组合期间键盘编辑与确认键由输入法处理，平台继续提供
组合更新与确认字符。`Keyboard.Text` 提供确认结果时替换原选区，并清空组合文字与组合光标；
`Keyboard.Composition(world, "", 0)` 取消组合显示。默认焦点转移、焦点节点释放和移除
`Focusable` 都通过 `CancelComposition` 结束原节点的组合显示，保留已提交文字与选区。

仅带 `Focused` 与 `EditableText` 的节点处理文本事件。输入在当前帧按注入顺序领取，
每条字符或组合事件作用于它之前的导航结果；失焦期间的事件在当次输入阶段结束。
多个 World 分别使用自己的缓冲、焦点和 Shift 状态。默认 `FocusNavigationSystem` 在
每条事件的位置直接调用无状态的 `TextEditingSystem.HandleInput`，将编辑与导航共用一份
事件序列。返回 true 表示该事件由编辑器领取，false 表示继续执行导航规则。替换默认导航
的用户系统也可以调用这两个公开方法，向 `HandleInput` 传入自己的选区扩展状态，并在
转移焦点时调用 `CancelComposition`。调度顺序继续沿上节的输入阶段运行。

`Edits` 仅在已提交文字内容变化时递增，采用 `uint` 回绕。光标、选区和组合显示的变化
保留该计数。`node.Edited(out string text)` 使用 `Scope.Read<EditableText>` 取得文本与计数，
通过 `Scope.Write<EditSeen>` 比较并保存该节点已经消费的计数，比较规则与 `Clicked` 一样
使用 `!=`。返回 true 表示有新编辑，一次读取消费当时的全部新计数；out 参数始终返回当前
已提交文本。同一作用域中的多次读取共用写入副本，成功计算结束时提交。

文本与值控件共享 `Edited` 名称时，通过 `out string text` 或 `out float value` 选择对应
行为；只消费计数时，分别写 `Edited(out string _)` 与 `Edited(out float _)`。

有效 World、非 null 输入字符串、组合光标范围，以及初始 `EditableText` 的文本元素位置
要求通过 Core `DevelopmentCheck` 报告，shipping 构建沿统一条件编译移除这些诊断。
参考独立 SomeUI 的完整文本元素移动与删除；本票按公开 `EditableText` 保存选区、独立组合
字符串与编辑计数，并通过公开作用域声明行为数据。

实现：[EditableText](../../../src/SomeEngine.Elements/EditableText.cs)、
[TextEditingFunctions](../../../src/SomeEngine.Elements/TextEditingFunctions.cs)、
[TextEditingSystem](../../../src/SomeEngine.Elements/TextEditingSystem.cs)。
公开验收：[TextEditingTests](../../../tests/SomeEngine.Elements.Consumer.Tests/TextEditingTests.cs)，
共享行为检查、开发检查和外部行为重写位于同一消费者工程，接入既有 Views NativeAOT 验收。

## 拖动、滚动与选择（#40）

```csharp
// SomeEngine.Elements
public struct Drag : IComponent
{
    public bool Active;
    public Vector2 Delta;
    public Vector2 Total;
    public uint Count;
}
public struct Selected : IComponent { public bool Value; }
public static class DraggableFunctions { public static Entity Draggable(Entity node); }
public static class ScrollableFunctions { public static Entity Scrollable(Entity node); }
public static class SelectableFunctions { public static Entity Selectable(Entity node); }

// SomeEngine.Layout，位于 Views 共享布局数据层
public struct ScrollOffset : IComponent { public Vector2 Value; public Vector2 Max; }
public struct ScrollInput : IComponent { public Vector2 Delta; public uint Count; }
```

三个行为函数在调用方作用域中以 `Ensure<T>` 声明数据，返回原节点。
Draggable 组合 Pressable 与 Drag；Scrollable 组合 Draggable、ScrollInput 与 ScrollOffset；
Selectable 组合 Pressable 与 Selected。重复构建保留系统写入的值。
用户可以用相同的公开组件与作用域函数声明自己的行为版本。

`HitTestSystem` 的拖动阶段在主按钮按下时，从命中节点及祖先中选取最近的 Drag 节点，
将它保存在同一份画布按下数据中，后续移动继续使用这个目标。第一次非零位移开始拖动，
`Count` 递增、`Total` 从零开始；`Active` 保持到释放或目标退出当前可交互树。
`Delta` 是本帧全部拖动位移之和，每次输入阶段从零开始；`Total` 是当前或最近一次拖动的
累计位移。静止帧保留 Active 和 Total。区域外的移动与释放继续送达原目标。
完成拖动的释放结束这次手势；点击由保持点击配对的手势产生。

滚轮增量使用画布单位，正值向内容末端移动。主按钮拖动以指针位移的反方向滚动。
输入阶段按事件顺序在上一帧的滚动范围内限制位移，把当帧实际变化写到 ScrollInput；
`Count` 记录接受的滚动输入次数。滚轮选择当前目标及祖先中最近的滚动节点。
子内容的命中区域沿祖先视口外框逐层取交集；捕获后的拖动继续送达按下时选定的目标。
布局阶段在内容的自然尺寸与当前视口之间计算 `Max`，将 `Value` 限制在 `[0, Max]`，
随后从子节点坐标减去 Value。ScrollOffset 整个组件由布局阶段写入，ScrollInput 由输入阶段写入；
布局记录已应用的输入计数，因此重复布局保留同一份输入的应用结果。
这份消费记录由布局单独持有，ScrollOffset 的公开写版本只随 Value 与 Max 的变化推进。
内容或视口缩小时，当前布局同时收紧范围和实际偏移。
布局测量、内边距、透明组合视口和嵌套坐标规则见
[[architecture/someui/Layout|Layout]] 的滚动布局章节。

每个完成的路由点击切换沿途 Selectable 节点的 `Selected.Value` 一次，包括同帧的多次点击。
`StopsPointer` 与画布边界按同一点击路由决定选择传播的范围。
选择数据由输入阶段写入，读取 Selected 的计算沿现有依赖机制更新。
这些行为共用 Pointer、画布命中与 InputCapture 的输入顺序。

布局写入的 ScrollOffset 通过写版本使下一次重建阶段读取它的计算更新。
独立 SomeUI 的 PanGestureRecognizer、GestureArena 与 ScrollSemantics 用于核对位移及范围语义；
本票按组件数据和现有画布输入阶段表达行为，拖动从第一次非零画布位移开始。

实现：[行为声明](../../../src/SomeEngine.Elements/ScrollableFunctions.cs)、
[拖动阶段](../../../src/SomeEngine.Elements/HitTestSystem.Drag.cs)、
[滚动输入](../../../src/SomeEngine.Elements/HitTestSystem.Scroll.cs)、
[共享滚动数据](../../../src/SomeEngine.Views/Layout/ScrollOffset.cs)。
公开验收：[BehaviorTests](../../../tests/SomeEngine.Elements.Consumer.Tests/BehaviorTests.cs)、
[内置与用户重写共用检查](../../../tests/SomeEngine.Elements.Consumer.Tests/BehaviorChecks.cs)。

## 数值编辑行为（#44）

```csharp
public record struct ValueRange(float Value, float Minimum = 0, float Maximum = 1, float Step = 0)
    : IComponent
{
    public readonly float Ratio { get; }
}
public record struct ValueDrag(Vector2 Direction, float Distance) : IComponent;
public struct ValueEdit : IComponent { public float Value; public uint Count; }
public struct ValueEditSeen : IComponent { public uint Count; }
```

`ValueFunctions.ValueDraggable(node, range, direction, distance)` 组合 `Draggable` 与
`Focusable`，写入调用方的范围和拖动映射，并声明编辑数据。方向在声明时归一化；
Distance 表示从 Minimum 到 Maximum 所需的画布位移。重复声明保留输入数据。
Value、Minimum、Maximum 与 Step 均要求有限，Minimum ≤ Value ≤ Maximum，Step ≥ 0，
方向有效且 Distance > 0；使用错误通过 Core `DevelopmentCheck` 报告。

HitTestSystem 的拖动阶段按指针事件顺序保存当前手势的起始值，用累计 `Drag.Total` 在 Direction 上的
投影计算候选值，再按范围和 Step 限制。小幅位移先累计，再量化，因此多次小移动可以
跨过量化阈值；区域外释放保留同一个捕获手势的结果。新手势以调用方当前值或此前尚待
领取的编辑值开始，同一输入帧中的多个手势按到达顺序应用。
当前输入结果与调用方数值分开持有：输入系统写 ValueEdit，调用方通过 Edited 写回应用数据。

当焦点节点声明 ValueRange 时，FocusNavigationSystem 将按下的 Right / Up 作为增加，
Left / Down 作为减少；每次重复按下对应一次步进，释放保持当前值。
Step > 0 时按 Step 步进，Step = 0 时按完整范围的 5% 步进。
数值节点的方向键编辑当前值，Gamepad.Navigate 继续执行原有焦点导航。
首次方向输入仍沿默认规则取得焦点，后续方向键编辑已聚焦节点。
Enter、Space 与 Gamepad.Confirm 继续提供焦点确认，Toggle 通过 Toggled 消费它们。

Step > 0 时，范围内候选值在以 Minimum 为原点的步长格点上取最近值，半步向增加方向取整；
范围端点保持可达。Step = 0 保留连续候选值。相同结果保持编辑计数，发生变化时递增
ValueEdit.Count。连续的键盘输入按缓冲顺序应用，并以本次尚待调用方领取的结果继续编辑。
`Edited(out value)` 读取 ValueEdit 并登记依赖，以 ValueEditSeen 消费新计数；成功构建结束
时按 Scope 的比较提交规则保存已读计数。读取 false 时，out 值为当前 ValueRange.Value。
Ratio 是当前参数在范围内的比例，等值范围返回 0，供用户控件选择自己的呈现。

实现：[ValueFunctions](../../../src/SomeEngine.Elements/ValueFunctions.cs)、
[数值输入](../../../src/SomeEngine.Elements/ValueEditing.cs)。
公开对等验收：[ValueChecks](../../../tests/SomeEngine.Elements.Consumer.Tests/ValueChecks.cs)、
[用户 Knob](../../../tests/SomeEngine.Elements.Consumer.Tests/Rewrites/Knob.cs)。

## 窗口输入转接（#42）

`SomeEngine.Runtime.WindowInput` 使用公开 `Pointer` 与 `Keyboard` 入口，
为一个 Game world 转接窗口输入。窗口事件由 `RuntimeInput.Process` 同时更新游戏输入
数据并交给该转接器，ImGui 继续沿自己的窗口事件入口接收输入。

转接器读取当前屏幕 `Canvas`，将窗口像素位置及滚轮增量除以该画布的 `Scale`，
再将移动、主键、次键、中键和滚轮按到达顺序送入对应画布。窗口离开时移动到画布外，
窗口失焦时在画布外释放各按钮，并送出键盘释放事件；已捕获的按下随释放结束。
`CanvasTarget.WorldSpace` 与纹理画布由各自的交互来源供给指针数据。

虚拟键 Left、Right、Up、Down、Enter、Space、Home、End、Shift、Backspace 与 Delete
对应 `Keyboard.Key`，按下、释放及平台重复按下分别转接。
`RuntimeInput` 从原有按键数据合并两侧 Shift，随后送出同一份选择状态。
`EnterCharacter` 组合窗口的 UTF-16 代理项，完整字符交给 `EnterText` → `Keyboard.Text`；
编辑控制字符沿键盘入口处理。`ChangeComposition` 将候选字符串与 UTF-16 光标位置交给
`Keyboard.Composition`，窗口失焦结束当前组合。

`NativeWindow` 接收 `WM_IME_STARTCOMPOSITION`、`WM_IME_COMPOSITION` 与
`WM_IME_ENDCOMPOSITION`，使用 IMM 读取预览、光标及确认字符串，按原有窗口事件队列转接。
确认字符串作为一个文本事件送到 SomeUI 与 ImGui。
该处理依据 [Microsoft 的 IMM 字符串读取说明](https://learn.microsoft.com/en-us/windows/win32/api/imm/nf-imm-immgetcompositionstringw)
及 [组合字符串确认路径](https://learn.microsoft.com/en-us/windows/win32/controls/em-setimestatus)。
`WindowInput.InputCapture` 从 World 单例读取当前捕获数据；在 `GameFrameSystems.UpdateInput`
之后，游戏摄像机将其与 ImGui 的捕获合并，分别控制键盘移动和鼠标旋转。

公开验收：[WindowInputTests](../../../tests/SomeEngine.Runtime.Tests/WindowInputTests.cs)。

## Relations
[[architecture/someui/Composition|Composition]] · [[architecture/someui/Data-Writers|Data Writers]] · [[architecture/someui/Rebuild-Schedule|Rebuild Schedule]]
