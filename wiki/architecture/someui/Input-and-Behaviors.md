# Input and Behaviors

## Definition
输入以数据表达：命中测试与路由系统写入节点上的指针数据，行为部件声明自己需要的数据，读取这些数据的计算因版本变化而执行。

## Invariants
- 点击是单调递增的计数 `Clicks`，与悬停、按下状态 `Hover` 分开存放，避免悬停变化使读取点击的计算重跑。`entity.Clicked()` 比较计数与该节点记录的已读值。
- 冒泡：路由系统沿 `ElementTree` 把点击计数写到目标及同一画布内带 `Pressable` 的祖先，处理带 `StopsPointer` 标记的节点后停止。
- 悬停：`Hover.Hovered` 表示命中目标；`Hover.Within` 表示指针命中该节点或同一画布内的后代。
- 输入归属：命中测试在游戏输入之前运行，结果写入单例 `InputCapture`，游戏系统据此决定是否处理本次输入。
- 焦点：全局一个实体带 `Focused`；`Focusable` 为标记；键盘与手柄导航由可通过系统调度替换的 `FocusNavigationSystem` 根据布局结果计算。
- 文本输入：平台把字符与输入法组合文本写入 `TextInput`，编辑逻辑由 `TextEditing` 行为对应的系统处理。
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
public enum Key : byte { Left, Right, Up, Down, Enter, Space }
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
public enum FocusInputKind : byte { Key, Navigate, Confirm }
public struct FocusInput : IBufferElement
{
    public FocusInputKind Kind;
    public Key Key;
    public bool Pressed;
    public Direction Direction;
}
public sealed class FocusNavigationSystem : ISystem<ImmediateSystemContext> { /* ... */ }
```

应用在输入阶段调用 `Keyboard` 和 `Gamepad`，事件按注入顺序追加到 World 单例
`InputCapture` 实体的 `FocusInput` 缓冲。首次输入或首次创建导航系统时建立默认单例和缓冲；
应用也可以事先创建自己的单例。导航系统在下一次输入阶段领取并清空事件。
`Key` 保存按下和释放两种变化；默认系统对每次 `pressed: true` 执行一次对应动作，
`pressed: false` 保持当前焦点与点击计数。平台的重复按键通过再次注入按下事件表达。
方向键执行导航，`Enter` 与 `Space` 执行确认。有效 World、已定义的键与方向，以及
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

## Relations
[[architecture/someui/Composition|Composition]] · [[architecture/someui/Data-Writers|Data Writers]] · [[architecture/someui/Rebuild-Schedule|Rebuild Schedule]]
