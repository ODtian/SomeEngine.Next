# Render Path

## Definition
界面渲染数据沿 Game world 到 Rendering world 的既有提取路径流动，并按更新频率区分持久部分与随变化更新的部分。

## Invariants
- 视觉都是材质加用户 shader。`Surface`（填充、圆角、描边、阴影，由 `Background`/`Corner`/`Border`/`Shadow` 修饰或复合容器 Build 写在参与布局的节点上）、`Image`、`Glyphs` 使用引擎自带的普通材质；渲染管线依赖用户 shader 解释绘制数据与顶点布局。
- 界面 pass 的拓扑在增删 `Canvas`、改变渲染目标或深度测试配置时通过显式修改维护，放在持久 RenderGraph 中。
- 实例数据按 ECS 写版本以块为单位增量提取，更新 Rendering world 中常驻的实例缓冲；静态界面不重新提取。
- 节点的最终呈现为布局结果、用户变换与 `PresentationOffset` 的叠加。
- 绘制顺序取树序；裁剪矩形位于实例数据中。
- 世界空间界面与屏幕界面使用同一套元素，`Canvas` 指向世界中的实体，由其变换定位。
- 不新增任何数据通路。

## Surface 提取（#32）

`SomeEngine.Elements.Render` 的 `CanvasRenderExtractor` 统一收集画布上的 Surface 与 Image，
`SurfaceRenderExtractor` 保留原有调用入口。应用在首次提取前将其中一个
安装到 `RenderExtractionSystems`。Surface 数据读取 `Canvas`、`Surface`、`LayoutRect`、
`ScrollOffset`、`ElementTransform`、`PresentationOffset` 及 `ElementTree` 的 Parent/Children
写版本，沿现有提取阶段收集。

每个 Canvas 的 RenderWorld 镜像实体持有 `RenderCanvas` 与一份 ECS
`SurfaceInstance` 缓冲。`RenderCanvas` 保存目标、像素尺寸与每布局单位像素数。
`SurfaceInstance` 的公开成员为 `Node`、`Rectangle`、`Fill`、`CornerRadius`、
`BorderColor`、`BorderWidth`、`ClipRectangle`、`DrawOrder`、`Version`、`Matrix` 与 `Alpha`。
`Shadow` 是附加的可初始化 DropShadow 属性，保留原有九参数构造与解构写法。
`Node` 标识来源实体，渲染消费者从缓冲取得其绘制数据。

阴影颜色、画布偏移与模糊半径和其余 Surface 字段在同一读取窗口收集，
初次提取和按行更新均写入同一 SurfaceInstance。渲染消费者通过自己的材质和 shader
解释这些参数。默认 DropShadow 表示透明阴影；清空或修改阴影沿同一增量提取路径送达。

矩形采用 `LayoutRect` 的画布坐标。基础裁剪范围为画布的布局单位边界：
`(0, 0, Canvas.Size.Width / Canvas.Scale, Canvas.Size.Height / Canvas.Scale)`。
祖先带有 ScrollOffset 时，后代的裁剪矩形进一步与其 LayoutRect 外框取交集；
嵌套视口逐层取交集，空交集给出零宽或零高的裁剪矩形。
树序采用 `ElementTree` 已应用的有序子节点做深度优先前序遍历。
`DrawOrder` 是该节点在遍历中的序号，Canvas 从零开始，透明组合节点同样占一个序号。
嵌套 Canvas 分别提取到自身缓冲。

组件写版本先筛选变化的 chunk，再按行版本选择 Surface、矩形与呈现数据更新。
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
`SurfaceShadowsReachRenderingOnCreationUpdateAndClear` 对两个公开提取入口应用同一组
阴影创建、更新与清空检查，观察 Rendering world 中的 SurfaceInstance 值。

此前独立 SomeUI 的 Scene、实例与 DrawOrderSystem 用于核对绘制数据及树序。
这里的实例缓冲采用引擎 ECS 存储，增量依据来自 ECS 写版本，并通过既有提取边界应用。

## 节点呈现（#52）

`CanvasRenderExtractor` 为 Surface 与 Image 读取呈现数据，`GlyphRenderExtractor`
在自己的字形提取路径读取同一节点数据。三个实例缓冲均保存 `Matrix` 与 `Alpha`，
缺省值分别为单位矩阵和一。原始布局矩形、图片适配区域、UV、字形颜色和图集数据
继续沿各自的数据规则维护。

用户变换以节点 `LayoutRect` 左上角为局部原点，`PresentationOffset` 使用画布坐标。
最终矩阵为 `Translation(-origin) * ElementTransform.Matrix * Translation(origin) * PresentationOffset.Matrix`，
透明度倍率为 `ElementTransform.Alpha * PresentationOffset.Alpha`。
图片扣除内边距并适配尺寸之后、字形按字形偏移得到矩形之后，仍使用节点外框作为用户变换原点。
呈现写版本按行选择实例更新；改变呈现数据保留布局、图片 UV 和字形图集的既有值。

Surface、Glyphs 与 Image 各自编码实例及解释 shader 字节，实例大小分别为
144、96、80 字节。Surface 在填充、描边、裁剪、形状和阴影数据之后保存矩阵与透明度；
Glyphs、Image 在各自的原有字段之后保存呈现数据。
shader 先把矩形点转换到呈现的画布坐标，再应用屏幕或世界画布的裁剪空间矩阵。
裁剪使用呈现的画布坐标；Surface 的圆角与描边覆盖率使用原矩形局部坐标。
Surface 先按自身覆盖率合成填充、描边及其后方阴影，再将完整预乘 RGBA 乘呈现透明度。
Glyphs、Image 分别将呈现透明度乘入字形覆盖率和纹理 alpha，并采用既有预乘混合规则。
同一画布的持久 pass 按原树序消费这些材质。

`PresentationExtractionTests` 经公开 World、提取与缓冲读取验证三种视觉的矩阵、透明度、
增量版本及提取后的值送达边界。`CanvasPresentationTests` 在 D3D12 WARP 和 Vulkan
的屏幕与世界画布上读回旋转、透明度、画布裁剪、静止帧和取消帧后的像素。

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
边缘覆盖率采用距离函数的屏幕导数，过渡宽度随投影取得一个目标像素。
裁剪矩形采用包含左上边界、排除右下边界的范围。
填充与描边的 RGBA 值在 shader 中乘覆盖率并转成预乘 alpha，
再以 `One` / `OneMinusSourceAlpha` 混合到已定义的目标颜色上。

Surface 材质同时解释实例中的 DropShadow。顶点范围覆盖表面与偏移后的阴影，
并包含模糊半径及一个目标像素的边缘余量。阴影沿同一圆角矩形距离函数计算：
正模糊半径使用 `1 - smoothstep(-BlurRadius, BlurRadius, distance)` 柔化边缘，
零半径使用一个目标像素的覆盖率过渡。shader 以表面剩余透明度合成阴影，
填充与描边位于自身阴影前方。表面与扩展的阴影范围共同应用呈现矩阵，再投影到画布目标；
同一裁剪矩形作用于两者呈现后的画布坐标，完整组合通过呈现透明度一起淡出。
实例字节布局由 Surface 材质与其 shader 共同解释。

Runtime 的 `SurfacePanelExample` 使用公开视图修饰与布局系统构建画布和面板，
沿同一 Game world → Rendering world 提取路径绘制。
DefaultGame 的测试平台默认显示该面板，`Surface panel` 选项控制示例；
调用 Runtime 参数入口时可使用 `--surface-panel`。
场景平台的结果文件记录该选项，供相同渲染条件的比较使用。

#42 将该面板扩展为公开 `SurfacePanelExample`：`Add one` 使用普通 `Button`、
`Focusable` 和 `Clicked()`，计数由同一份 `State<int>` 持有并显示为 `Clicks: N`。
窗口尺寸改变后通过视图参数与 Canvas 数据更新位置，计数继续由原状态持有。
方向键取得按钮焦点，Enter 与 Space 确认同一按钮。面板与 ImGui 调试界面共用当前
呈现目标；F1 控制 ImGui，`Surface panel` 控制 SomeUI 示例。
公开验收：[CounterPanelTests](../../../tests/SomeEngine.Runtime.Tests/CounterPanelTests.cs)。

DefaultGame 的标准 HUD、共享文字资源和三场景测量由 [Performance](Performance.md#标准-hud-与统一测量68) 维护。

`tests/SomeEngine.RenderGraph.Tests/CanvasDrawingTests` 经公开结构版本、pass 身份和
GPU 像素读回验收画布成员修改、静态帧、改色、取消帧、透明树序叠加、裁剪与画布移除。
相同像素用例分别使用 D3D12 WARP 和 Vulkan，并启用引擎图形验证层。
`CanvasShadowTests` 同样通过两种后端的像素读回验收阴影半透明颜色、柔化边缘、
正负偏移、裁剪、参数更新与清空，以及表面对自身阴影的覆盖。

此前独立 SomeUI 的距离函数及填充/描边覆盖区域用于核对材质语义。
本实现采用引擎的持久图与实例光栅绘制，实例数据由既有 RenderWorld 缓冲提供。

## Image（#35）

```csharp
public enum ImageFit : byte { Contain, Stretch, Cover }
public record struct Image(AssetId<TextureAsset> Source, ImageFit Fit = ImageFit.Contain)
    : IView, IComponent, ILayout;
public sealed class ImageTextures(Func<AssetId<TextureAsset>, Texture?> resolve)
    : ILayoutDataUpdate;
```

`Source` 采用资产层当前的强类型身份 `AssetId<TextureAsset>`。应用通过 `ImageTextures`
提供已就绪的 `SomeEngine.Resources.Texture`，并把它注册到 `LayoutSystem` 的内容更新入口。
纹理的异步取得与使用期限由应用的资产加载流程持有。这个入口将就绪纹理、尺寸和内容版本
送入 Game world，随后布局与渲染提取消费同一份节点数据。

Image 的自然尺寸为纹理宽高，父约束与 Box 沿统一布局协议限制它。
资源尚在取得时，自然尺寸采用统一缺失纹理的 1×1；Box 可指定显示区域。
纹理就绪、替换或内容版本更新会触发布局输入更新，并在同一帧送达提取。

`LayoutRect` 保存布局分配的外框。绘制区域扣除 Box 内边距。
Stretch 使用整个内容区域和 `(0, 0, 1, 1)` UV；Contain 保持纹理比例，
把完整图片居中放入内容区域；Cover 使用整个内容区域，居中裁剪纹理 UV。
零尺寸区域保留零面积绘制数据。

`CanvasRenderExtractor` 通过既有 Game world → Rendering world 提取阶段，
统一维护 Canvas 镜像及 `SurfaceInstance`、`ImageInstance` 缓冲。
`SurfaceRenderExtractor` 保留为同一提取者的原有调用入口。实例包含来源节点、就绪纹理、绘制矩形、
UV 矩形、画布裁剪、树序和绘制版本。数据更新与结构更新沿 ECS 写版本处理。

`ImageMaterial` 编译自身的 Slang shader，并解释自己的实例字节。
Surface、Glyphs 与 Image 的内嵌 shader 由 ElementShaders 统一持有，每个图形目标各编译一份。
这些不可变 shader 供材质与管线引用，使用期限覆盖进程中的画布绘制。
Slang 的全局对象由这一份数据保持有效，其派生对象按
[Slang 生命周期规则](https://github.com/shader-slang/slang/blob/master/docs/user-guide/08-compiling.md)
保持在全局对象的使用期限内。
`CanvasRenderer` 接受 Image 材质与应用的 `RenderDevice`，在现有画布 pass 中
按树序组合 Surface、Glyphs 与 Image。同一节点依次绘制 Surface、Glyphs、Image。
shader 从 `SV_VertexID` 生成矩形，以纹理 RGBA 转成预乘 alpha 后混合到目标。
纹理使用线性过滤与边界夹取，裁剪采用画布布局坐标。
Image 与 Surface 在同一次画布遍历中继承滚动视口的裁剪交集，视口外框变化更新对应实例。
图片沿同一画布变换应用屏幕坐标或世界空间投影，并采用画布选择的场景深度测试。
每个批次绑定由材质给出的实例缓冲读取范围，从该范围的首条实例开始。
连续同材质、同纹理的实例使用批绘；范围视图与实例缓冲一起沿图的完成边界回收。

纹理上传与设备视图采用 `RenderDevice.PrepareTexture` / `GetTextureSrv`；
取得期间采用 `PrepareMissingTexture` / `MissingTextureSrv` 的统一紫色纹理。
纹理绑定和上传属于当帧数据；画布成员与目标通过显式编辑维护持久图。
正常纹理流送以已驻留的 Bootstrap 表示进行绘制。

`tests/SomeEngine.Elements.Render.Tests/ImageTests` 经公开视图函数、World、布局、
提取及实例缓冲验证固有尺寸、横图/竖图的三种填充、纹理就绪、内边距、零尺寸与开发检查。
`tests/SomeEngine.RenderGraph.Tests/ImageDrawingTests` 经完整视图到画布路径，在
D3D12 WARP 与 Vulkan 上读回图片、缺失纹理、透明叠加和混合树序的像素，
并验证取消首用帧与 GC 后继续绘制。

此前独立 SomeUI 的 ImageSource 和 Image 视图作为元素构成参考。
本票使用引擎的强类型资产身份、布局协议、提取阶段与统一纹理设备资源。
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
以 `SV_InstanceID` 读取自身实例字节。Surface、Glyphs 与 Image 在同一个画布 pass 中，
按树序及图集页形成连续绘制范围，分别绑定各自的 shader 和参数。
材质按自己的实例字节大小建立各绘制范围的缓冲视图，shader 使用相对的 `SV_InstanceID`。
这与 [Slang 的 SPIR-V 实例编号规则](https://docs.shader-slang.org/en/latest/external/slang/docs/user-guide/a2-01-spirv-target-specific.html)
及当前 D3D12 编译目标采用同一套实例寻址，连续范围从对应缓冲视图的起点开始。
内置 shader 编译结果由 `ElementShaders` 按名称与目标统一持有，供应用生命周期内的材质共用。
原有 Surface、Surface/Glyphs 与 Surface/Image 构造入口继续接入这一份画布绘制器。
同时绘制三种材质时，调用方传入 GlyphsMaterial、ImageMaterial 和 RenderDevice：

```csharp
using var canvases = new CanvasRenderer(backend, device, graph, renderWorld,
    surfaces, outputFormat, glyphs, images, renderDevice);
```

实例按节点树序排列，同一节点依次绘制 Surface、Glyphs、Image；
每个材质用自己的实例范围、纹理或图集页形成连续批次。
字形图集与图片纹理的上传均属于当帧图工作，消费它们的画布 pass 保持持久身份。
管线顶点布局沿用 shader 自己解释实例的规则。

Glyphs 以线性采样读取单通道 SDF：`(sample * 255 - 128) * DistanceSpread / 128`
还原图集像素距离，按字形大小与画布缩放转换到目标像素，
转换比例从插值 UV 的屏幕导数取得，随画布的屏幕变换与世界投影变化；
`saturate(0.5 + distance)` 形成一个目标像素宽的抗锯齿覆盖率。
裁剪采用包含左上、排除右下的画布矩形；颜色乘覆盖率并转成预乘 alpha，
与 Surface 使用相同的 `One` / `OneMinusSourceAlpha` 混合。
相比此前独立 SomeUI 的 RGBA/MSDF 参考，本实现直接消费 Typography 的单通道距离值、
真实 `DistanceSpread` 与按像素/em 给出的字形几何。

Runtime 的屏幕面板使用随资产提供的 Noto 字体子集显示 `Hello SomeEngine` 与
`中文你好世界`，字体及许可证位于 `assets/Fonts/`。
公开 World 验收位于 `GlyphExtractionTests`；D3D12 WARP 与 Vulkan 的覆盖图读回
位于 `GlyphDrawingTests`，参考图从字体轮廓独立生成，生成方式与容差位于测试图像目录。
`CanvasCompositionTests` 通过公开 RenderWorld、实例缓冲、图集页和纹理数据，
在 D3D12 WARP 与 Vulkan 的屏幕/世界画布上读回交错的 Surface、文字与图片。
用例同时修改图集区域和图片纹理，取消更新帧后重新绘制，并验证 GC、裁剪、深度与图结构保持。
## 世界空间画布（#36）

`CanvasTarget.WorldSpace(entity)` 指向 Game world 中持有 `WorldTransform` 的目标。
`CanvasRenderExtractor` 在同一提取读取窗口内取得目标变换，写入画布镜像的
`RenderCanvas.Transform`，并提取 `Canvas.DepthTest`。目标的变换写版本单独选择
画布常量更新；实例矩形仍在画布坐标中，沿同一 Surface 缓冲和材质送达。
目标平移、旋转、缩放与拉伸按 [[architecture/someui/Layout|Layout]] 的坐标约定生效。

绘制调用为 `ReconcileGraph(screenTarget, new WorldCanvasTarget(color, depth, view))`。
`view` 是 RenderWorld 中的 `RenderView` 实体；场景颜色和深度均为现有图资源。
世界画布使用视图的 viewport，材质把画布坐标经 Scale、目标变换、View 与 Projection
转换到裁剪空间。屏幕画布由同一材质使用像素到裁剪空间的矩阵。
矩阵与 viewport 经当帧数据接口更新，目标变换与视图移动保留 pass 身份和图结构版本。

`DepthTest` 为 true 时，pass 读取场景深度附件，以 `WorldCanvasTarget.DepthFormat`
与 `DepthComparison` 创建材质管线；默认 D32Float / LessOrEqual，匹配当前场景管线。
使用反向深度的场景可以显式选择 GreaterOrEqual。
附件按只读深度声明，场景深度值继续供后续消费者使用。
画布成员、目标与深度配置变化只维护对应画布 pass；其它画布继续使用原有 pass。
同一颜色目标上的屏幕画布显式排序在世界画布之后。
应用可通过独立的世界颜色目标将画布放在所需场景阶段，默认示例在呈现后叠加。

Runtime 的 `SphereNameplates` 在 1024 小球场景中选择若干运动实体，为它们建立
世界画布。编号名牌由普通布局与 Surface 数字段组合，画布目标变换在 Game world
随小球更新，然后进入已有提取阶段。每个 World 的示例共用一次视图重建与布局阶段。
DefaultGame 默认启用 `World nameplates`，结果文件记录 `WorldNameplates`；
Runtime 参数入口通过 `--world-nameplates` 启用。

公开提取测试验证目标的平移、旋转、缩放、拉伸与值送达边界。
`WorldCanvasDrawingTests` 在 D3D12 WARP 与 Vulkan 开启图形验证层，读回像素验证
投影位置、两种深度约定下的可选遮挡、前后位置变化、视图移动、透视投影、画布缩放与结构版本保持。

## Relations
[[architecture/rendering/Extraction|Extraction]] · [[architecture/Render-Graph|Render Graph]] · [[architecture/someui/Layout|Layout]]
