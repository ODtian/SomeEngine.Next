# Render Path

## Definition
界面渲染数据沿 Game world 到 Rendering world 的既有提取路径流动，并按更新频率区分持久部分与随变化更新的部分。

## Invariants
- 视觉都是材质加用户 shader。`Surface`（填充、圆角、描边、阴影，由 `Background`/`Corner`/`Border`/`Shadow` 修饰或复合容器 Build 写在参与布局的节点上）、`Image`、`Glyphs` 使用引擎自带的普通材质；渲染管线不识别控件，也不预设顶点布局。
- 界面 pass 的拓扑只在增删 `Canvas` 或改变渲染目标时变化，放在持久 RenderGraph 中通过显式修改维护。
- 实例数据按 ECS 写版本以块为单位增量提取，更新 Rendering world 中常驻的实例缓冲；静态界面不重新提取。
- 节点的最终呈现为布局结果、用户变换与 `PresentationOffset` 的叠加。
- 绘制顺序取树序；裁剪矩形位于实例数据中。
- 世界空间界面与屏幕界面使用同一套元素，`Canvas` 指向世界中的实体，由其变换定位。
- 不新增任何数据通路。

## Surface 提取（#32）

`SomeEngine.Elements.Render` 提供 `SurfaceRenderExtractor`，应用在首次提取前将它
安装到 `RenderExtractionSystems`。它声明读取 `Canvas`、`Surface`、`LayoutRect`
及 `ElementTree` 的 Parent/Children 写版本，沿现有提取阶段收集数据。

每个 Canvas 的 RenderWorld 镜像实体持有 `RenderCanvas` 与一份 ECS
`SurfaceInstance` 缓冲。`RenderCanvas` 保存目标、像素尺寸与每布局单位像素数。
`SurfaceInstance` 的公开成员为 `Node`、`Rectangle`、`Fill`、`CornerRadius`、
`BorderColor`、`BorderWidth`、`ClipRectangle`、`DrawOrder` 与 `Version`。
`Shadow` 是附加的可初始化 DropShadow 属性，保留原有九参数构造与解构写法。
`Node` 标识来源实体，渲染消费者从缓冲取得其绘制数据。

阴影颜色、画布偏移与模糊半径和其余 Surface 字段在同一读取窗口收集，
初次提取和按行更新均写入同一 SurfaceInstance。渲染消费者通过自己的材质和 shader
解释这些参数。默认 DropShadow 表示透明阴影；清空或修改阴影沿同一增量提取路径送达。

矩形采用 `LayoutRect` 的画布坐标。当前裁剪范围为画布的布局单位边界：
`(0, 0, Canvas.Size.Width / Canvas.Scale, Canvas.Size.Height / Canvas.Scale)`。
树序采用 `ElementTree` 已应用的有序子节点做深度优先前序遍历。
`DrawOrder` 是该节点在遍历中的序号，Canvas 从零开始，透明组合节点同样占一个序号。
嵌套 Canvas 分别提取到自身缓冲。

组件写版本先筛选变化的 chunk，再按行版本选择 Surface 与矩形更新。
树成员、子节点顺序和 Canvas 数据变化时，重新整理受影响画布的实例序列。
实例从版本一开始，仅在绘制数据变化时推进，调用方以 `!=` 比较版本以支持回绕。
相等绘制数据保留实例与缓冲写版本；静态帧保留缓冲值与写版本。
节点释放、Surface 移除或 LayoutRect 移除后，缓冲移除对应实例。
Canvas 数据移除时释放它的实例缓冲，镜像上的其它渲染数据继续由各自的提取系统维护。

有序层级读取在同一个提取读取窗口内完成；收集值经 RenderPrepare 更新边界及
ECS 结构事务送入常驻缓冲。常驻绘制数据由 RenderWorld 的 ECS 缓冲持有，
当次收集数据供该次提取应用。

`tests/SomeEngine.Elements.Render.Tests` 经公开 World、层级、视图修饰、布局系统、
提取调度、帧协调器和缓冲读取验证单节点改色、静态帧、树序、释放、组件移除与恢复、
跨画布移动、布局与缩放更新，以及提取后 Game world 继续修改时的值送达边界。

此前独立 SomeUI 的 Scene、实例与 DrawOrderSystem 用于核对绘制数据及树序。
这里的实例缓冲采用引擎 ECS 存储，增量依据来自 ECS 写版本，并通过既有提取边界应用。

## Relations
[[architecture/rendering/Extraction|Extraction]] · [[architecture/Render-Graph|Render Graph]] · [[architecture/someui/Layout|Layout]]
