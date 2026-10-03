# Layout

## Definition
布局采用协议：布局数据（视图参数）实现 `ILayout`，布局系统按类型分派，不识别具体布局。
模型为约束向下、尺寸向上、父节点摆放子节点。父布局在一次系统更新内完成测量与摆放，
可按基础尺寸与最终分配尺寸传入不同约束测量同一子节点。

```csharp
public interface ILayout : IView
{
    abstract Size IView.Measure(ref LayoutPass pass, in Constraints constraints);
}

public ref struct LayoutPass
{
    public Entity Node { get; }
    public LayoutChildren Children { get; }                      // 已跳过透明节点
    public Size Measure(Entity child, in Constraints constraints);
    public void Place(Entity child, Vector2 position);
    public ref readonly T Read<T>(Entity entity) where T : struct;
    public bool Has<T>(Entity entity) where T : struct;
}
```

## Invariants
- 多种布局并存且对等：内置 `Flex`、`Grid`、`Stack`、`Absolute`，叶子 `Text`、`Image` 在自己的 `Measure` 中测量内容；用户布局（例如环形布局）写法相同。`Row()`、`Column()` 是产出 `Flex` 的普通函数。
- 分派经 `ViewType` 的类型表取得 `Measure` 函数指针，不装箱。没有实现 `ILayout` 的节点对布局透明，其子节点直接参与上一级布局。
- 嵌套不受限制：父布局只调用 `pass.Measure(child, ...)`。
- 子节点的布局参数由父布局类型定义（`FlexItem`、`GridCell`、`AbsolutePosition`），由调用方通过修饰写入。
- `Box` 的尺寸限制与内边距由 `LayoutPass` 在每个节点自身 `Measure` 前后统一处理。
- `LayoutRect` 由布局系统写入，记录节点外框的画布坐标。缓存与固定尺寸重排边界由 #27 接入。
- 根节点带 `Canvas` 组件，记录渲染目标（屏幕、render texture 或世界空间实体）、尺寸与缩放；分屏与世界空间 UI 即多个 `Canvas`。

## 布局协议实现（#24）

`SomeEngine.Layout` 提供 `LayoutSystem`、`Absolute` 与 `LayoutModifiers`，依赖 Views。
共享测量协议、几何约束、节点数据与当次布局数据放在 Views 的基础层，命名空间为
`SomeEngine.Layout`。这样构建与测量共用一个 `ViewType` 类型表，程序集依赖沿
Layout → Views → ECS/Core 流动。`Rect` 与 `Thickness` 使用 `SomeEngine.Core.Math`。

`IView` 提供默认测量接缝，`ILayout` 将同一成员声明为必需实现。
布局视图提供 `public Size Measure(ref LayoutPass, in Constraints)`，或显式实现
`IView.Measure`。泛型视图注册为实现 `ILayout` 的类型登记测量函数指针，
测量时读取实际的表格或 sparse 参数，以泛型约束调用其方法。
`LayoutChildren` 提供 `Count`、索引器与 `foreach`，按声明顺序展开透明节点。
参与布局的节点使用自己的 Box 和父布局参数；透明组合的后代直接使用上级布局。

每个父布局测量其直接布局子节点，并通过 `Place` 写入相对于内容原点的位置。
同一子节点的再次测量更新布局系统持有的同一份当次尺寸与后代位置，父布局使用最后测量的尺寸摆放它。
布局视图的每次测量都测量并摆放自己的全部布局子节点。
父布局返回后按树序累计坐标并写入
`LayoutRect`；这一步只转换坐标。布局系统在每次更新结束时清空当次数据。

```csharp
public readonly record struct Size(float Width, float Height);
public readonly record struct Constraints(Size Min, Size Max);
// Constraints.Fixed(size) 固定两轴；constraints.Constrain(size) 将尺寸限制在区间内。
```

`Box` 的 `Padding` 为 `Thickness`，`Min` 默认零，`Max` 默认 `Size.Unbounded`，
`Fixed` 默认 `Size.Auto`（两轴为 NaN）。`Max` 与 `Fixed` 使用属性，使
`default(Box)` 保持这些默认值；零仍是有效的固定尺寸。`Fixed` 的任一轴可以单独为 NaN。
父约束先限制 Box 的最小、最大与固定尺寸，再扣除内边距传给视图的 `Measure`；
返回内容尺寸后加回内边距并限制到外框区间。`Place` 自动加上左、上内边距。

`Absolute` 的 `Children` 为 `Caller`。它读取子节点的 `AbsolutePosition`，
默认位置为零，按内容可用尺寸减去子节点位置得到其最大尺寸；最小尺寸为零。
子节点外框的最大右、下边界给出容器的内容尺寸，再按父约束限制。
调用方可使用生成的 `Absolute()` 同名函数，导入 `AbsoluteFunctions`。

`Canvas.Size` 为渲染目标的像素尺寸，`Scale` 为每画布单位的像素数，默认 1。
画布可用尺寸为 `Size / Scale`，布局根以固定约束取得整个画布尺寸。
根视图通过 `Views.Spawn(world, canvasEntity, view)` 挂入 `ElementTree`。
多个根在同一画布原点开始布局；每个 Canvas 的坐标从零开始，嵌套 Canvas 作为独立画布处理。
`CanvasTarget.Screen` 表示屏幕，`RenderTexture(entity)` 与 `WorldSpace(entity)`
分别保存纹理实体与世界实体。Canvas 配置由应用写入。

帧顺序为 `ViewRebuildSystem` → `LayoutSystem`。
开发构建通过 Core 的 `DevelopmentCheck` 报告尺寸、约束、缩放及测量/摆放使用错误，
这些检查调用随 shipping 编译消失。

## Flex（#25）

```csharp
public enum Axis : byte { Horizontal, Vertical }
public enum Align : byte { Auto, Start, Center, End, Stretch, SpaceBetween }
public record struct Flex(Axis Direction, float Gap = 0,
    Align MainAlign = Align.Start, Align CrossAlign = Align.Stretch, bool Wrap = false)
    : IView, IComponent, ILayout;
public struct FlexItem : IComponent
{
    public float Grow;
    public float Shrink { get; set; } // 默认 1
    public float Basis { get; set; }  // 默认 NaN，采用基础测量尺寸
    public Align Self;
}
public static partial class FlexFunctions
{
    public static Entity Row(float gap = 0);
    public static Entity Column(float gap = 0);
}
```

`Flex.Children` 为 `Caller`。`Row(gap)` 和 `Column(gap)` 是普通视图函数，
分别声明 `View(new Flex(Axis.Horizontal, gap))` 和 `View(new Flex(Axis.Vertical, gap))`。
生成的 `Flex(...)` 同名函数也使用同一视图参数与测量协议。
调用方通过 `Scope.Write<Flex>` 或容器修饰写入参数，通过 `Scope.Write<FlexItem>`
或子节点修饰写入伸缩数据。`FlexItem` 使用属性保持 `default(FlexItem)` 的
`Shrink = 1`、`Basis = NaN`；显式零表示零收缩权重或零基础尺寸。
`Self = Align.Auto` 继承容器的交叉轴对齐。

主轴对齐支持 `Start`、`Center`、`End` 和 `SpaceBetween`；交叉轴对齐与自身对齐
支持 `Start`、`Center`、`End` 和 `Stretch`。`SpaceBetween` 将正的剩余空间
加到已有行内 Gap 上，单个子节点从主轴起点开始。
居中与终点对齐使用容器长度减去该行实际长度计算偏移。

Flex 先在主轴可扩展、交叉轴受父约束的区域测量子节点。
`Basis` 为 NaN 时采用这次外框主轴尺寸，包含子节点内边距与 Box 固定尺寸；
显式 Basis 则在 Box 的主轴最小、最大尺寸之间取值。
容器采用各行的最大主轴长度，并遵循父约束；存在正 Grow 且主轴上限有限时采用该上限。
每行的剩余空间按 Grow 权重分配，溢出按 Shrink 权重回收。
两种权重都直接参与分配；子节点到达 Box.Min 或 Box.Max 后，其余空间继续由可伸缩节点分配。
Box 固定主轴尺寸提供自动 Basis，最终伸缩尺寸作为固定主轴约束传回子节点。
这次测量让嵌套布局与叶子内容取得最终可用尺寸，也决定该行的内容高度或宽度。

`Wrap = true` 时，按声明顺序累积基础主轴尺寸与 Gap；下一项超过可用主轴上限时另起一行。
各行分别伸缩、对齐，行的交叉轴长度取最终测量的最大子节点长度，行间也使用 Gap。
`Wrap = false` 时，该行交叉轴长度采用容器内容长度。
自动交叉轴尺寸在 `Stretch` 下拉伸到该行长度，并遵循 Box 的交叉轴上下限；
固定交叉轴尺寸保留自己的大小。自动尺寸容器由内容确定行高，因此分隔条可跟随相邻控件拉伸。

开发构建通过 `DevelopmentCheck` 报告方向、对齐、Gap、伸缩权重、Basis
和容器修饰的使用错误；检查调用随 shipping 编译消失。

此前独立 SomeUI 的伸缩、对齐、Gap、内边距与换行用例以公开 API 移植。
本实现使用 ILayout 类型协议与统一 Box 约束，伸缩下限由显式 `Box.Min` 表达，默认零。
为保证伸缩后的嵌套布局正确，本票将 #24 的单次子测量限制改为同一节点数据上的重复测量。

## 公开行为验收

`tests/SomeEngine.Layout.Tests` 通过 World、视图构建、修饰、系统组与 `LayoutRect`
验证画布绝对定位、同帧内边距更新、透明组合、用户稀疏环形布局嵌套、尺寸限制、
默认 Box、零尺寸、单轴自动尺寸、独立画布与缩放。
`LayoutChecks.cs` 在单元测试与 `just views-aot win-x64` 中共用，验证显式测量实现、
稀疏布局参数、内置布局嵌套及画布尺寸更新。
`FlexLayoutTests.cs` 验证横纵轴伸缩、基础尺寸、尺寸限制后的再分配、Gap、主轴与交叉轴对齐、
自身对齐、分隔条、换行、嵌套 Row/Column 与同帧修饰更新。
`FlexChecks.cs` 在单元测试与 NativeAOT 中共用，验证生成的 Flex 同名函数、
Row/Column、按行 Grow、Basis、Gap 与画布尺寸更新。

此前独立 SomeUI 的布局源码用于核对内边距和内容边界；本实现采用本页的类型协议，
测量和子节点摆放都由布局视图负责。

## Relations
[[architecture/someui/Modifiers|Modifiers]] · [[architecture/someui/Render-Path|Render Path]] · [[architecture/someui/Lists|Lists]]
