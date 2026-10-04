# Render Path

## Definition
界面渲染数据沿 Game world 到 Rendering world 的既有提取路径流动，并按更新频率区分持久部分与随变化更新的部分。

## Invariants
- 视觉都是材质加用户 shader。`Surface`（填充、圆角、描边，由 `Background`/`Corner`/`Border` 修饰写在参与布局的节点上）、`Image`、`Glyphs` 使用引擎自带的普通材质；渲染管线不识别控件，也不预设顶点布局。
- 界面 pass 的拓扑只在增删 `Canvas` 或改变渲染目标时变化，放在持久 RenderGraph 中通过显式修改维护。
- 实例数据按 ECS 写版本以块为单位增量提取，更新 Rendering world 中常驻的实例缓冲；静态界面不重新提取。
- 节点的最终呈现为布局结果、用户变换与 `PresentationOffset` 的叠加。
- 绘制顺序取树序；裁剪矩形位于实例数据中。
- 世界空间界面与屏幕界面使用同一套元素，`Canvas` 指向世界中的实体，由其变换定位。
- 不新增任何数据通路。

## Surface 提取（#32）

`SomeEngine.Elements.Render` 提供 `SurfaceRenderExtractor`，应用在首次提取前将它
安装到 `RenderExtractionSystems`。它声明读取 `Canvas`、`Surface`、`LayoutRect`、`ScrollOffset`
及 `ElementTree` 的 Parent/Children 写版本，沿现有提取阶段收集数据。

每个 Canvas 的 RenderWorld 镜像实体持有 `RenderCanvas` 与一份 ECS
`SurfaceInstance` 缓冲。`RenderCanvas` 保存目标、像素尺寸与每布局单位像素数。
`SurfaceInstance` 的公开成员为 `Node`、`Rectangle`、`Fill`、`CornerRadius`、
`BorderColor`、`BorderWidth`、`ClipRectangle`、`DrawOrder` 与 `Version`。
`Node` 标识来源实体，渲染消费者从缓冲取得其绘制数据。

矩形采用 `LayoutRect` 的画布坐标。基础裁剪范围为画布的布局单位边界：
`(0, 0, Canvas.Size.Width / Canvas.Scale, Canvas.Size.Height / Canvas.Scale)`。
祖先带有 ScrollOffset 时，后代的裁剪矩形进一步与其 LayoutRect 外框取交集；
嵌套视口逐层取交集，空交集给出零宽或零高的裁剪矩形。
树序采用 `ElementTree` 已应用的有序子节点做深度优先前序遍历。
`DrawOrder` 是该节点在遍历中的序号，Canvas 从零开始，透明组合节点同样占一个序号。
嵌套 Canvas 分别提取到自身缓冲。

组件写版本先筛选变化的 chunk，再按行版本选择 Surface 与矩形更新。
树成员、子节点顺序和 Canvas 数据变化时，重新整理受影响画布的实例序列。
视口外框变化，以及 ScrollOffset 的添加或移除，同样刷新后代的裁剪矩形。
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
`ScrollClippingTests` 覆盖嵌套视口、外框变化、滚动声明增删与静止帧的裁剪数据。

此前独立 SomeUI 的 Scene、实例与 DrawOrderSystem 用于核对绘制数据及树序。
这里的实例缓冲采用引擎 ECS 存储，增量依据来自 ECS 写版本，并通过既有提取边界应用。

## 屏幕画布绘制（#33）

`SomeEngine.Elements.Render.CanvasRenderer` 从 RenderWorld 消费 `RenderCanvas` 与
`SurfaceInstance` 缓冲。应用在 RenderPrepare 已提交之后调用
`ReconcileGraph(screenTarget)`：每个屏幕画布持有一个持久 raster pass 及其视图。
画布成员或屏幕目标改变时，通过一个显式 RenderGraph 编辑添加、删除或重接对应 pass。
其它 pass 的身份继续有效。`TryGetPass` 提供画布 pass 的身份，供应用声明额外排序关系。

取得图帧后，`UpdateFrame(ref frame)` 通过 `SetBufferInput` 提供实例缓冲，
通过 `SetBufferData` 写画布尺寸与缩放，并通过 `SetPassDataIfChanged` 更新绘制数量和 viewport。
画布常量采用图管理的独立逐帧上传缓冲；这些缓冲只由本画布的 graphics pass 消费，
与场景呈现一起满足交换链 acquisition 的单次提交边界。
Runtime 将呈现排序在 Cluster 的 page-fault 读回之后，使呈现、界面与图像导出位于
流送反馈提交边界之后，共同完成该次交换链图像使用。
Canvas 尺寸、缩放与实例值属于当帧数据，保留图的结构版本。
实例更新由 ECS 缓冲写版本筛选；静态帧复用已有的 GPU 实例缓冲。
变化时，Surface 将当前实例编码为 GPU 缓冲，旧缓冲交由图的
`RetireAfterSubmittedFrames` 在已提交帧完成后回收。
帧执行回调保存图返回的实际缓冲状态及完成点，供后续帧导入同一缓冲。
取消图帧后，该次编码的数据仍可供下一帧绘制。
画布绘制器释放时，通过显式编辑删除自身 pass 与视图，缓冲和管线沿同一完成边界回收。

`SurfaceMaterial` 是普通 `Material`。`Create` 编译内嵌 `Surface.slang`，生成与
图设备对应的 DXIL 或 SPIR-V。材质的 Surface 绘制实现负责实例编码和 shader 接口。
shader 以 `SV_VertexID` 生成矩形的三角形顶点，以 `SV_InstanceID` 读取自己的实例字节；
管线的顶点缓冲布局和顶点属性声明为空。图只声明实际缓冲读取和颜色目标访问。
绘制顺序采用提取缓冲的树序。

矩形、圆角、描边和裁剪采用画布布局单位，`RenderCanvas.Scale` 将其转换为目标像素。
圆角半径约束到短边的一半，描边位于矩形内部。
shader 以圆角矩形的距离函数计算覆盖率；填充与内部描边分别覆盖形状的内部与环带，
边缘覆盖率的过渡宽度为一个目标像素。
裁剪矩形采用包含左上边界、排除右下边界的范围。
填充与描边的 RGBA 值在 shader 中乘覆盖率并转成预乘 alpha，
再以 `One` / `OneMinusSourceAlpha` 混合到已定义的目标颜色上。

Runtime 的 `SurfacePanelExample` 使用公开视图修饰与布局系统构建画布和面板，
沿同一 Game world → Rendering world 提取路径绘制。
DefaultGame 的测试平台默认显示该面板，`Surface panel` 选项控制示例；
调用 Runtime 参数入口时可使用 `--surface-panel`。
场景平台的结果文件记录该选项，供相同渲染条件的比较使用。

`tests/SomeEngine.RenderGraph.Tests/CanvasDrawingTests` 经公开结构版本、pass 身份和
GPU 像素读回验收画布成员修改、静态帧、改色、取消帧、透明树序叠加、裁剪与画布移除。
相同像素用例分别使用 D3D12 WARP 和 Vulkan，并启用引擎图形验证层。

此前独立 SomeUI 的距离函数及填充/描边覆盖区域用于核对材质语义。
本实现采用引擎的持久图与实例光栅绘制，实例数据由既有 RenderWorld 缓冲提供。

## 文字提取与绘制（#34）

应用创建一个持有 CPU 图集的 `TextAtlas` 实体，并把该实体交给
`GlyphRenderExtractor`。应用同时安装 `SurfaceRenderExtractor`，后者继续写入
画布的 `RenderCanvas`；字形提取写入 `GlyphInstance` 缓冲与 `CanvasGlyphAtlas`。

```csharp
Entity textAtlas = gameWorld.CreateEntity(new TextAtlas(new GlyphAtlas()));
using var extraction = new RenderExtractionSystems(renderWorld,
    new SurfaceRenderExtractor(), new GlyphRenderExtractor(textAtlas));
using SurfaceMaterial surfaces = SurfaceMaterial.Create(device.Capabilities.CompileTarget);
using GlyphsMaterial glyphs = GlyphsMaterial.Create(device.Capabilities.CompileTarget);
using var canvases = new CanvasRenderer(backend, device, graph, renderWorld,
    surfaces, outputFormat, glyphs);
```

`ShapedText` 的最终字形与样式、`LayoutRect`、Text 节点自身的 `Box.Padding`
生成字形实例。位置为节点左上角加内边距、排版给出的基线位置、按字号缩放的图集偏移；
尺寸为图集字形矩形乘以 `ShapedText.Style.Size / GlyphAtlas.FontSize`。
`GlyphInstance` 提供 `Node`、`GlyphIndex`、`Rectangle`、`UV0`、`UV1`、`Color`、
`ClipRectangle`、`PageIndex`、`DrawOrder` 与 `Version`。
`GlyphIndex` 是排版字形序列的位置。空白字形保留排版推进，绘制实例只包含有图集矩形的字形。
树序、画布坐标与裁剪规则沿用 Surface 提取；同节点的背景先于文字。
祖先滚动视口的矩形逐层取交集；视口尺寸变化时重算后代字形的裁剪。
字形布局及矩形按 chunk 与行的写版本筛选；实例数据相等时保留 ECS 缓冲写版本。
文字由空变为可见、树重排、嵌套画布及节点释放沿同一提取入口更新。

图集源的镜像实体持有 `RenderGlyphAtlas`，画布的 `CanvasGlyphAtlas.Atlas` 指向它。
`RenderGlyphAtlas` 保存 `Pages`、`DirtyRegions`、`DistanceSpread` 和 `Version`。
每个 `GlyphPagePixels` 提供尺寸与只读单通道页像素。
提取在同一 Game world 读取窗口内复制变化页的像素，再清空 CPU 图集新增区域。
完整页数据供首次绘制及渲染跨过多次提取后的补齐；当前变化矩形供连续帧增量上传。
所有页引用与实例值在 RenderPrepare 边界提交，RenderWorld 持有这份绘制数据。
改色时只更新字形颜色与实例版本，排版版本及图集组件、页像素和图集版本保持原值。

每个图集页对应常驻的 `R8UNorm` GPU 纹理。首次使用时上传整页，使字形间的零值边界
也具有定义的内容；连续提取版本按 `DirtyRegions` 矩形上传。跨过多个图集版本时，
完整页数据补齐对应纹理。上传行距与起始偏移由 RHI 的纹理复制足迹决定。
区域复制属于当帧图工作，画布 raster pass 保留持久身份及结构版本。
画布绘制显式排序在相关区域复制之后，GPU 页状态与完成点由图帧提交结果接续。
取消帧后，已收集像素继续供下一帧上传；已提交的复制决定 GPU 图集版本。

`GlyphsMaterial` 是普通 Material；内嵌 `Glyphs.slang` 以 `SV_VertexID` 生成矩形，
以 `SV_InstanceID` 读取自身实例字节。Surface 与 Glyphs 在同一个画布 pass 中，
按树序及图集页形成连续绘制范围，分别绑定各自的 shader 和参数。
材质按自己的实例字节大小建立各绘制范围的缓冲视图，shader 使用相对的 `SV_InstanceID`。
这与 [Slang 的 SPIR-V 实例编号规则](https://docs.shader-slang.org/en/latest/external/slang/docs/user-guide/a2-01-spirv-target-specific.html)
及当前 D3D12 编译目标采用同一套实例寻址，连续范围从对应缓冲视图的起点开始。
内置 shader 编译结果由 `ElementShaders` 按名称与目标统一持有，供应用生命周期内的材质共用。
管线顶点布局沿用 shader 自己解释实例的规则。

Glyphs 以线性采样读取单通道 SDF：`(sample * 255 - 128) * DistanceSpread / 128`
还原图集像素距离，按字形大小与画布缩放转换到目标像素，
`saturate(0.5 + distance)` 形成一个目标像素宽的抗锯齿覆盖率。
裁剪采用包含左上、排除右下的画布矩形；颜色乘覆盖率并转成预乘 alpha，
与 Surface 使用相同的 `One` / `OneMinusSourceAlpha` 混合。
相比此前独立 SomeUI 的 RGBA/MSDF 参考，本实现直接消费 Typography 的单通道距离值、
真实 `DistanceSpread` 与按像素/em 给出的字形几何。

Runtime 的屏幕面板使用随资产提供的 Noto 字体子集显示 `Hello SomeEngine` 与
`中文你好世界`，字体及许可证位于 `assets/Fonts/`。
公开 World 验收位于 `GlyphExtractionTests`；D3D12 WARP 与 Vulkan 的覆盖图读回
位于 `GlyphDrawingTests`，参考图从字体轮廓独立生成，生成方式与容差位于测试图像目录。

## Relations
[[architecture/rendering/Extraction|Extraction]] · [[architecture/Render-Graph|Render Graph]] · [[architecture/someui/Layout|Layout]]
