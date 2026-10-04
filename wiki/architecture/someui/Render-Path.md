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
安装到 `RenderExtractionSystems`。它声明读取 `Canvas`、`Surface`、`LayoutRect`
及 `ElementTree` 的 Parent/Children 写版本，沿现有提取阶段收集数据。

每个 Canvas 的 RenderWorld 镜像实体持有 `RenderCanvas` 与一份 ECS
`SurfaceInstance` 缓冲。`RenderCanvas` 保存目标、像素尺寸与每布局单位像素数。
`SurfaceInstance` 的公开成员为 `Node`、`Rectangle`、`Fill`、`CornerRadius`、
`BorderColor`、`BorderWidth`、`ClipRectangle`、`DrawOrder` 与 `Version`。
`Node` 标识来源实体，渲染消费者从缓冲取得其绘制数据。

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
Surface 与 Image 的内嵌 shader 由共享编译数据持有，每个图形目标各编译一份。
这些不可变 shader 供材质与管线引用，使用期限覆盖进程中的画布绘制。
Slang 的全局对象由这一份数据保持有效，其派生对象按
[Slang 生命周期规则](https://github.com/shader-slang/slang/blob/master/docs/user-guide/08-compiling.md)
保持在全局对象的使用期限内。
`CanvasRenderer` 接受 Image 材质与应用的 `RenderDevice`，在现有画布 pass 中
按树序组合 Surface 与 Image。同一节点先绘制 Surface，再绘制 Image。
shader 从 `SV_VertexID` 生成矩形，以纹理 RGBA 转成预乘 alpha 后混合到目标。
纹理使用线性过滤与边界夹取，裁剪采用画布布局坐标。
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

## Relations
[[architecture/rendering/Extraction|Extraction]] · [[architecture/Render-Graph|Render Graph]] · [[architecture/someui/Layout|Layout]]
