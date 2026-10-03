# Layout

## Definition
布局采用协议：布局数据（视图参数）实现 `ILayout`，布局系统按类型分派，不识别具体布局。模型为约束向下、尺寸向上、父节点摆放子节点的单遍计算。

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
当次尺寸与位置存放于布局系统持有的数据，父布局返回后按树序累计坐标并写入
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

## 公开行为验收

`tests/SomeEngine.Layout.Tests` 通过 World、视图构建、修饰、系统组与 `LayoutRect`
验证画布绝对定位、同帧内边距更新、透明组合、用户稀疏环形布局嵌套、尺寸限制、
默认 Box、零尺寸、单轴自动尺寸、独立画布与缩放。
`LayoutChecks.cs` 在单元测试与 `just views-aot win-x64` 中共用，验证显式测量实现、
稀疏布局参数、内置布局嵌套及画布尺寸更新。

此前独立 SomeUI 的布局源码用于核对内边距和内容边界；本实现采用本页的类型协议，
测量和子节点摆放都由布局视图负责。

## Relations
[[architecture/someui/Modifiers|Modifiers]] · [[architecture/someui/Render-Path|Render Path]] · [[architecture/someui/Lists|Lists]]
