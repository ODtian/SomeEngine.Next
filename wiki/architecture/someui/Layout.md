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
- `LayoutRect` 由布局系统写入，记录节点外框的画布坐标。约束与布局输入相同时复用 `LayoutCache`，固定尺寸约束形成重排边界。
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
同一子节点的再次测量更新布局系统持有的同一份测量结果与后代相对位置，父布局使用最后测量的尺寸摆放它。
布局视图的每次测量都测量并摆放自己的全部布局子节点。
父布局返回后按树序累计坐标并写入
`LayoutRect`；这一步只转换坐标。布局系统跨帧保留读取依赖、布局子节点顺序和相对位置，
当次遍历列表在更新结束时清空。

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

## 内容测量数据（#31）

`LayoutSystem` 接受 `params ILayoutDataUpdate[]`，保留公开无参构造函数。
`ILayoutDataUpdate` 位于共享布局协议层，提供
`HasChanges(World, Entity)`、`Update(World, Entity, in Constraints)` 与默认空实现的 `Complete(World)`。
`HasChanges` 将内容输入变化纳入节点布局输入版本；默认返回 true，数据更新者可按自己的输入版本缩小范围。
Text 的更新者比较文本、样式与外观的公开 ECS 写版本，内容变化与布局约束共同决定重新测量。
布局在每次测量前，把扣除 Box 内边距后的内容约束交给这些数据更新者；
画布的测量、摆放与坐标写入完成后调用 `Complete` 提交最终内容结果。
各布局继续通过同一 `ILayout` 类型分派和 `LayoutPass.Measure` 测量自己的子节点。

文本系统通过这一入口按内容宽度写入字形结果，`Text.Measure` 读取结果尺寸；
接口、系统接入、版本与缓冲期限由 [[architecture/someui/Text|Text]] 维护。
图片通过 `ImageTextures` 在同一入口取得应用已就绪的纹理与固有尺寸；
资源更新、布局尺寸与填充语义由 [[architecture/someui/Render-Path|Render Path]] 维护。
`LayoutRect` 写入比较最终矩形，相同矩形保持组件写版本。

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

`Basis` 为 NaN 时，Flex 在主轴可扩展、交叉轴受父约束的区域测量子节点，
采用这次外框主轴尺寸，包含子节点内边距与 Box 固定尺寸。
显式 Basis 为初测提供固定的主轴约束，再在 Box 的主轴最小、最大尺寸之间取值；
按可用空间测量的用户布局可通过 `Basis(0).Grow()` 取得有限的初测与最终约束。
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

## 布局缓存与重排边界（#27）

```csharp
public struct LayoutCache : IComponent
{
    public Constraints Constraints;
    public Size Size;
    public uint InputVersion;
}
```

`LayoutCache` 与 `LayoutRect` 同属 Views 基础层，使用 `SomeEngine.Layout` 命名空间。
每个参与布局的视图实体保存最后一次测量的传入约束、外框尺寸和已消费的布局输入版本，
由布局系统写入。应用通过 World 读取，清除布局节点的 `LayoutCache` 后，下一次布局重新测量；
清除全部布局节点的缓存可用于全量对照。

布局系统为每个节点持有当前输入版本、组件读取依赖、展开透明组合后的有序布局子节点和相对位置。
视图参数与 Box 自动登记读取；用户布局通过 `LayoutPass.Read<T>` 和 `Has<T>`
登记额外的表格或 sparse 组件值与存在性。这些读取可以来自当前节点、子节点或外部实体。
每帧比较公开 ECS 写版本与存在性，再核对布局子节点的增删、顺序和所属布局父节点。
发生变化的输入自底向上传递，更新节点的布局输入版本。
测量时，传入约束与缓存一致且输入版本相同，便复用外框尺寸及子节点相对位置。

节点在父布局的一次计算中接受的每组约束，经 Box 限制后均固定两轴时，
其后代布局尺寸变化在该节点止步。该节点使用上次的传入约束单独测量内容，父布局继续使用其固定外框。
节点自身的 Box、视图参数、父约束以及父布局直接读取的数据变化，仍按相应输入更新。
多次测量保留本次计算的读取依赖，并共同确定重排边界：
Flex 的松约束基础测量仍贡献自然尺寸，因此内容变化继续影响基础尺寸和伸缩分配；
最终分配的固定约束与这些基础测量共同决定缓存失效范围。
这一规则使增量布局与按相同约束全量测量取得相同结果。

父节点移动时，保留的相对位置在树序遍历中重新累计为画布坐标。
每个 Canvas 的根从自身原点开始，嵌套 Canvas 使用独立的可用尺寸与坐标。
`LayoutRect` 只在矩形值变化时写入，外围矩形保持原值时也保持公开 ECS 写版本。
布局系统释放离开全部画布布局树的读取与位置记录，重新进入布局树的节点重新建立测量数据。

实际执行测量的布局仍通过 `Measure` 取得全部布局子节点尺寸，再通过 `Place` 摆放。
`Place` 的使用错误与遗漏子测量通过 Core 的 `DevelopmentCheck` 报告，
这些检查调用及遍历随 shipping 编译消失。

## Stack 与 Grid（#26）

```csharp
public record struct Stack(Align Horizontal = Align.Start, Align Vertical = Align.Start)
    : IView, IComponent, ILayout;
public record struct Grid(GridTracks Rows, GridTracks Columns, float Gap = 0)
    : IView, IComponent, ILayout;
public enum GridTrackKind : byte { Fixed, Auto, Fraction }
public readonly record struct GridTrack(GridTrackKind Kind, float Value = 0)
{
    public static GridTrack Fixed(float size);
    public static GridTrack Auto { get; }
    public static GridTrack Fraction(float weight = 1);
}
public readonly struct GridTracks : IEquatable<GridTracks>
{
    public GridTracks(params ReadOnlySpan<GridTrack> tracks);
    public int Count { get; }
    public GridTrack this[int index] { get; }
}
public struct GridCell : IComponent
{
    public int Row, Column;
    public int RowSpan { get; set; }     // 默认 1
    public int ColumnSpan { get; set; }  // 默认 1
}
```

两种视图的 `Children` 为 `Caller`，调用方通过生成的 `Stack(...)`、`Grid(...)`
同名函数或 `Scope.View(new ...)` 声明容器，使用 `.Content()` 声明子节点。
布局沿同一 `ILayout` 测量协议处理透明组合、Box 和嵌套布局。

Stack 叠放子节点，内容尺寸取所有子节点外框的最大宽度和最大高度，再遵循父约束。
水平与垂直方向独立支持 `Start`、`Center`、`End`、`Stretch`。
起点、居中和终点对齐按容器内容尺寸与子节点外框尺寸计算位置。
`Stretch` 将子节点的自动轴拉伸到容器内容尺寸，并遵循 Box 与父约束；固定轴保持测量尺寸。
拉伸后的子节点按最终约束重新测量，使其后代取得分配后的尺寸。
`new Stack()` 与生成的 `Stack()` 均默认两轴 `Start`。
此前独立 SomeUI 的 `StackLayout` 表达按方向依次排列；本票按规格实现叠放式 Stack，
按方向依次排列由 Flex、Row 和 Column 提供。

GridTracks 复制输入定义并按轨道值比较相等，支持集合表达式，默认值为空序列。
`Fixed(size)` 定义有限非负长度，`Auto` 从内容测量尺寸，`Fraction(weight)` 定义有限正权重。
Grid 先测量子节点外框并确定列宽，再按列宽重新测量子节点以确定行高，
最后按单元格尺寸测量并摆放全部子节点。

自适应轨道按跨度从小到大处理内容，同一跨度按子节点声明顺序处理。
单轨道取最大内容尺寸；跨轨道内容超过当前轨道与内部 Gap 的总长度时，
多出的长度平均加到覆盖范围内的 Auto 轨道，固定轨道保持其定义长度。
存在有限轴上限时，比例轨道按权重分配扣除固定、自适应轨道和 Gap 后的非负剩余空间。
轴上限为无界时，覆盖比例轨道的内容确定所需的比例单位，全部比例轨道使用最大的单位，
保持轨道间的权重比例。父布局随后分配有限尺寸时，Grid 按新约束再次测量。
Grid 内容尺寸为轨道长度与轨道间 Gap 的总和，再遵循父约束；固定和自适应轨道保留内容长度，
比例轨道在剩余空间为零时采用零长度。

GridCell 的行列从零开始，默认位于 `(0, 0)`，两种跨度均默认为 1。
每个单元格及跨度覆盖范围须落在显式行列定义内；调用方通过
`.Cell(row, column, rowSpan = 1, columnSpan = 1)` 写入 GridCell。
同一单元格允许叠放多个子节点。
跨度尺寸包含所跨轨道之间的 Gap，位置从对应行列的起点开始。
子节点的自动轴填满单元格并遵循 Box.Max，固定轴按 Box 与单元格约束测量；
子节点摆放在单元格起点，内边距沿统一 Box 路径处理。

开发构建通过 Core 的 `DevelopmentCheck` 报告 Stack 对齐、轨道定义、Grid.Gap
与单元格坐标/跨度错误。不可变轨道在创建时检查，可写参数在测量入口检查；
这些检查调用随 shipping 编译消失。

## 公开行为验收

`tests/SomeEngine.Layout.Tests` 通过 World、视图构建、修饰、系统组与 `LayoutRect`
验证画布绝对定位、同帧内边距更新、透明组合、用户稀疏环形布局嵌套、尺寸限制、
默认 Box、零尺寸、单轴自动尺寸、独立画布与缩放。
`LayoutChecks.cs` 在单元测试与 `just views-aot win-x64` 中共用，验证显式测量实现、
稀疏布局参数、内置布局嵌套及画布尺寸更新。
同一用例还验证局部尺寸更新、外围矩形版本保持，以及清除缓存后的全量测量结果。
`FlexLayoutTests.cs` 验证横纵轴伸缩、基础尺寸、尺寸限制后的再分配、Gap、主轴与交叉轴对齐、
自身对齐、分隔条、换行、嵌套 Row/Column 与同帧修饰更新。
`FlexChecks.cs` 在单元测试与 NativeAOT 中共用，验证生成的 Flex 同名函数、
Row/Column、按行 Grow、Basis、Gap 与画布尺寸更新。
`LayoutCacheTests.cs` 通过公开视图、World、ElementTree、布局系统、`LayoutRect`
及组件写版本验证紧约束边界、画布切换、父节点移动、额外组件读取和稀疏视图参数。
四个随机种子连续修改视图参数、修饰、画布尺寸与缩放，并增删、移动和重排子节点；
每轮将增量矩形与清除缓存后的全量矩形逐一比较。

`StackLayoutTests.cs` 验证叠放尺寸、两轴对齐、自动轴拉伸与 Box 尺寸限制。
`GridLayoutTests.cs` 验证固定、自适应和比例轨道、Gap、跨度、轨道值比较、
同帧参数更新、Grid/Flex 双向嵌套及宽度变化后的换行高度。
`StackGridChecks.cs` 在单元测试与 NativeAOT 中共用，验证生成的 Stack/Grid 同名函数、
默认 Stack、嵌套 Flex 与画布尺寸更新。

此前独立 SomeUI 的布局源码用于核对内边距和内容边界；本实现采用本页的类型协议，
测量和子节点摆放都由布局视图负责。

## Relations
[[architecture/someui/Modifiers|Modifiers]] · [[architecture/someui/Render-Path|Render Path]] · [[architecture/someui/Lists|Lists]]
