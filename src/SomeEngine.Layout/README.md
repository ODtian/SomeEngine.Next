# SomeEngine.Layout

从 Canvas 的视图根测量布局，写入画布坐标的 `LayoutRect`。
将 `LayoutSystem` 放在 `ViewRebuildSystem` 后，每个父布局通过 `LayoutPass`
测量并摆放自己的子节点。用户视图实现 `ILayout`，使用与内置 `Absolute`、`Flex`、`Stack`、`Grid` 相同的类型分派。

```csharp
using static SomeEngine.Layout.AbsoluteFunctions;
using SomeEngine.Layout;
using SomeEngine.Views;

// 在视图的 Build 中：
using (Absolute().Padding(16).Content())
{
    Absolute().Size(100, 50).Position(10, 20);
    Absolute().MinSize(80, 40).MaxSize(200, 120).Position(200, 20);
}
```

```csharp
using static SomeEngine.Layout.FlexFunctions;

using (Row(10).Padding(16).Align(Align.Start, Align.Center).Content())
{
    Absolute().Size(100, 40);
    Absolute().Size(4, float.NaN).Shrink(0).AlignSelf(Align.Stretch);
    using (Column(6).Grow().Basis(120).Content())
        Absolute().Size(float.NaN, 30);
}
```

`Flex` 支持横纵方向、Gap、主轴和交叉轴对齐以及换行。
子节点通过 `Grow`、`Shrink`、`Basis` 和 `AlignSelf` 写入 `FlexItem`。
Flex 按行分配尺寸后将最终约束传给子节点，使嵌套布局随分配结果更新。

```csharp
using static SomeEngine.Layout.StackFunctions;
using static SomeEngine.Layout.GridFunctions;

using (Stack(Align.Center, Align.Center).Content())
using (Grid([GridTrack.Auto, GridTrack.Fraction()],
    [GridTrack.Fixed(120), GridTrack.Fraction(2)], Gap: 10).Content())
{
    Absolute().Size(100, 40);
    using (Row(6).Cell(1, 1).Content())
    {
        Absolute().Size(0, 30).Grow();
        Absolute().Size(0, 30).Grow();
    }
}
```

`Stack` 按两轴对齐叠放子节点。`Grid` 通过不可变的 `GridTracks` 描述行列，
支持固定、自适应和比例轨道；`.Cell()` 选择单元格及行列跨度。
比例轨道分配固定轨道、自适应内容与 Gap 之后的剩余空间，
嵌套布局通过最终单元格约束取得自己的可用尺寸。

协议、约束与节点组件使用 `SomeEngine.Layout` 命名空间，由 Views 基础层提供，
使构建与测量共用视图类型表，并保持 Layout → Views 的程序集依赖。
`Rect` 和 `Thickness` 来自 Core。应用持有 Canvas 的目标、尺寸和缩放；
修饰通过 `Scope.Write` 提交，布局系统持有 `LayoutRect` 的写入职责。

布局系统写入 `LayoutCache` 保存最后的约束、外框尺寸与布局输入版本。
相同约束和输入复用测量，固定尺寸子树的内部变化由重排边界局部处理。
用户布局通过 `LayoutPass.Read` 与 `Has` 读取的组件也参与失效判断。
矩形值变化时推进 `LayoutRect` 的 ECS 写版本；清除布局节点上的 `LayoutCache` 后重新测量。

详细接口、尺寸语义、透明组合与公共行为验收见
[Layout](../../wiki/architecture/someui/Layout.md)。

`LayoutSystem.Parallel` 默认 true，按画布根与重排边界运行独立子树的 Job。
设置 false 使用同一测量算法顺序执行。输入读取与最终 ECS 提交由系统线程完成。
内容更新者通过 `SupportsParallel`、`ReadContent` 和 `ILayoutContent.Update`
提供节点的约束测量数据，`Complete` 提交最终内容结果。
原有 `ILayoutDataUpdate.Update` 接入继续采用顺序测量。
共享公开分块入口为 `ChunkJobs.Run`；详细执行规则见
[子树 Job 测量](../../wiki/architecture/someui/Layout.md#子树-job-测量69)。
