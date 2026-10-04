# Input and Behaviors

## Definition
输入以数据表达：命中测试与路由系统写入节点上的指针数据，行为部件声明自己需要的数据，读取这些数据的计算因版本变化而执行。

## Invariants
- 点击是单调递增的计数 `Clicks`，与悬停、按下状态 `Hover` 分开存放，避免悬停变化使读取点击的计算重跑。`entity.Clicked()` 比较计数与该节点记录的已读值。
- 冒泡：路由系统把事件计数写到目标及其祖先，遇到 `StopsPointer` 标记停止，不使用回调链。
- 输入归属：命中测试在游戏输入之前运行，结果写入单例 `InputCapture`，游戏系统据此决定是否处理本次输入。
- 焦点：全局一个实体带 `Focused`；`Focusable` 为标记；键盘与手柄导航由可通过系统调度替换的 `FocusNavigationSystem` 根据布局结果计算。
- 文本输入：平台把字符与输入法组合文本写入 `TextInput`，编辑逻辑由 `TextEditing` 行为对应的系统处理。
- 行为部件（`Pressable`、`Draggable`、`Focusable`、`Scrollable`、`TextEditing`、`Selectable`）用 `Ensure<T>` 声明数据，可挂到任意节点。

## 指针输入实现（#37）

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
public struct Hover : IComponent { public bool Hovered; public bool Pressed; }
public struct Clicks : IComponent { public uint Count; }
public struct ClickSeen : IComponent { public uint Count; }
public struct Pressable : IComponent { }
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
该目标的 `Clicks.Count` 递增一次。重复释放保持当前计数。
`Hover.Hovered` 表示当前命中目标，`Hover.Pressed` 表示该节点持有至少一个按钮的按下，
移动到区域外时继续保留按下，直到释放。目标退出当前画布的可交互树后，按下目标清空。
下一帧按新布局重新判断悬停，因此静止指针也能观察布局移动。
滚轮事件经过相同命中与输入捕获阶段，当前指针路径消费它的事件缓冲。

`PressableFunctions.Pressable(node)` 用 `Scope.Ensure` 声明 `Hover`、`Clicks` 与标记，
并返回原节点。调用方可以用相同公开组件和作用域函数实现自己的版本。
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
[用户重写](../../../tests/SomeEngine.Elements.Consumer.Tests/Rewrites/PressableFunctions.cs)。

## Relations
[[architecture/someui/Composition|Composition]] · [[architecture/someui/Data-Writers|Data Writers]] · [[architecture/someui/Rebuild-Schedule|Rebuild Schedule]]
