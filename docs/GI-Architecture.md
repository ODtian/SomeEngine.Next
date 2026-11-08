# GI 架构

本页记录用户已确认的 GI 架构。确认来源为[逐级追踪原对话](codex://threads/01a16282-9df6-7220-b574-10f52c4fd8d1)与[效果及接入讨论](codex://threads/01a171a9-8c87-7ca2-8e56-593115439e0f)。

## 追踪主链

软件 GI 采用 Lumen 形式的逐级追踪：

**屏幕空间追踪 → 近景 Mesh SDF → 后续射线段的 Global SDF。**

这是三种追踪方式的逐级接续。命中确认发生在各自分支内；近景与 Global SDF 分支都会使用物体内的三角指针。

| 追踪方式 | 查询数据 | 命中处理 |
| --- | --- | --- |
| 屏幕空间追踪 | 当前主表面的深度和已有三角编号 | 用已有表面信息确认候选命中 |
| 近景 Mesh SDF | 各个物体的局部距离场 | 进入表面附近后，通过该物体的三角指针查询原三角形 |
| Global SDF | 合并后的场景距离场 | 进入潜在表面区间后，Object Grid 提供候选物体，再通过这些物体的三角指针确认命中 |

每一级将尚需确认的射线区间交给下一级。近景候选加速服务于 Mesh SDF 查询；完整阶段顺序由统一追踪入口持有。

Object Grid 属于 Global SDF 分支内的物体定位。三角指针属于表面定位与求交机制，提供原始三角形、重心坐标和材质解释所需的命中信息。

## 光照职责

Screen Trace 承担屏幕空间求交。Screen Probe 承担采样、方向入射辐射的保存与复用；像素使用自身材质法线完成漫反射积分。

GI 采用拉模式：材质着色主动查询方向辐射缓存，以当前表面的位置、几何法线、材质法线和漫反射参数积分，直接将间接光合成到材质颜色。缓存更新负责产生并保存可查询的方向辐射，颜色输出由既有材质着色路径持有。

SHARC 与 NRC 承担次级命中表面的后续光照复用，训练目标、查询结果和光路范围需要一致。接收面的历史、方向辐射与次级表面光照分别由对应表示持有。

主表面接受太阳、点光源、发光几何体和天空环境的照明，归直接光管线；发光几何体直接照明按用户指定的 MegaLights 风格处理。GI 消费间接光路所需的次级表面光照，并负责反射传播。

SomeEngine 实验接入直接复用引擎现有的直接光与 VSM。材质在同一次着色中完成光源求值和 VSM 采样，GI 读取其中已包含阴影的漫反射辐射。直接光阴影暂缓实现的约定适用于 Python 原型阶段；引擎接入与性能对照沿用现有 VSM 配置。

引擎接入遵循[两个 World](../wiki/architecture/rendering/GameWorld-and-RenderWorld.md)与[材质 Shader](../wiki/architecture/Assets/Asset-Shaders.md)的既有数据职责。引擎已有运动向量输出及其时域消费入口见 [Motion](../wiki/architecture/features/Motion.md)。

## 性能依据

目标设备包含缺少硬件光追的显卡。优化沿完整主链评估，结合画质、运动响应、实际追踪量、三角形测试量和 GPU 耗时，优先删除无效工作并减少总体工作量。材质查询消费已有探针方向辐射数据。

验证规范由[根级 AGENTS](../AGENTS.md)与[性能测量](../wiki/harness/Harness-Performance-Evidence.md)维护。

渲染性能运行 DefaultGame 的 Release NativeAOT 发布产物，沿用 `SomeEngine.Game.props` 的 `PublishAot=true`、`PublishAotUsingRuntimePack=true` 和 `SelfContained=true`。当前已有性能参照使用 Forward、1280×720、VSM/TAA 开启，场景面板、世界标签和 HUD 关闭；按同一份场景行为及采样窗口比较 GI 开关。界面启用与统计通过现有 `ApplicationScene` 选项控制。

## 架构变更

架构变更以用户对具体内容的明确同意为实施前提。涉及追踪阶段职责、数据表示、数据归属或主调用链的变更，先说明具体改动、收益依据与代价，再取得用户明确同意。

持续优化在当前已确认架构内进行，优先复用已经完成的计算和已有数据，减少重复查询、搬运与调度，并共同核对误差、实际工作量和耗时。

## 引擎实验入口

实验接入位于 `codex/experimental-gi-main` 分支，工作树目录为 `F:/.codex/worktrees/experimental-gi/SomeEngine.Next`，基于 `origin/main` 的 `13864df6`。本会话后续 GI 修改与运行以该工作树的 SomeEngine 为基础，直接复用引擎管线。

D3D12 的设备能力读取保留已有 `D3D12Options1` 查询结果，将 `TotalLaneCount` 和每组 1024 线程的上限提供给既有 Cluster 派发公式。性能对照以这些能力赋值齐全的代码为基础，沿用管线已经实现的多工作组派发。

当前引擎入口为 [ScreenProbeFeature](../src/SomeEngine.Render.Features/GI/ScreenProbeFeature.cs)，通过 `ApplicationSettingsBlock.GlobalIlluminationShader` 选择已有 Shader 资产加载通路，通过 `--experimental-gi` 或 DefaultGame 的 `Experimental GI` 选项启用。材质资源通过已有 `IGraphResourceProvider` 连接，帧输入来自 `ClusterPipelineOutputs`。

当前实验接入实现屏幕阶段：

- [材质着色](../assets/Shaders/standard_material.slang)通过 [SampleGiDiffuse](../assets/Shaders/gi_probe_sampling.slang) 读取已有探针方向辐射，使用当前材质的位置、法线和漫反射颜色完成积分并合成。同一次着色将几何法线、材质法线、漫反射颜色和包含 VSM 的表面漫反射辐射提供给屏幕追踪。
- DefaultGame 的 [SampleMaterials](../samples/DefaultGame/SceneContent/SampleMaterials.cs)通过已有 `ShaderSourceBlock.SetSerializedSource` 提供带 GI 输出的标准材质 IR，继续组合引擎的 `ClusterMaterialEntriesBlock`。
- [屏幕探针](../assets/Shaders/gi_screen_probes.slang)读取引擎深度、可见三角编号和运动向量，每个 8×8 像素区域至多追踪一条方向射线，保存双帧 SH9 方向辐射。候选表面的平面与投影后的可见三角编号共同确认屏幕命中。
- 材质查询按当前像素的 motion vector 定位上一帧的四个邻近探针，并核对表面位置和几何法线。`SurfacePixelInput.MotionVector` 直接接收 `WriteClusterMotion` 已经计算并写入运动纹理的同一结果。
- 首帧与相机切换先初始化探针缓存。材质着色读取上一帧的探针层；当帧屏幕追踪在材质着色后消费表面输出和运动纹理，更新本帧探针层，供下一帧材质查询。
- [帧执行](../src/SomeEngine.Runtime/RuntimeApplication.cs)直接将既有 `MaterialColor` 交给颜色功能、Temporal 与呈现。图拓扑由 `Connect` 显式建立；GI 常量复用 [FeatureUniformBuffer](../src/SomeEngine.Render.Features/FeatureUniformBuffer.cs) 的固定图资源和读取范围，当帧调用更新常量内容与历史状态。

屏幕阶段沿用 Cluster 的坐标转换：`ClipToWorld` 还原出相对场景数据原点的位置，投影和像素尺度计算通过 `ClusterPositionForView` 转到视图原点。历史表面位置通过已有 `PreviousDataTranslation` 换到当前场景数据原点，再参与表面核对。

Mesh SDF 的资产准备通过现有 glTF 导入器集合生成 `MeshDistanceFieldBlock`。数据包含物体局部距离采样、局部三角形网格与指向现有几何页的三角指针；几何位置来自 `ClusterGeometryBlock`，三角形顺序使用既有打包结果。烘焙阶段使用临时 CPU BVH 求最近三角距离，生成后释放。每个网格资产保存一份该数据，运行期的资源接入随实际 GPU 追踪阶段实施。

`MeshAsset` 是按 Block 组合不同表示的资产容器，资产系统负责身份、按需读取和字节复用；`Mesh` 是已创建的几何使用对象，直接持有几何描述及就绪数据。使用方从资产取得自己认识的 Block，再创建对应使用对象。Mesh SDF 接入沿用这一资产读取方式，距离场表示通过 `MeshDistanceFieldBlock` 提供。

Game world 中的资产引用直接采用已有的 `AssetId<TAsset>` 组件。网格与骨架分别使用 `AssetId<MeshAsset>`、`AssetId<SkeletonAsset>`；网格的范围扩张、阴影分类和第一人称设置由 `MeshInstanceSettings` 保存。场景序列化通过泛型资产编号 codec 记录外部引用，沿用 WorldAsset 的依赖收集。

[Mesh SDF 查询](../assets/Shaders/gi_mesh_distance_field.slang)实现最多八次保守跳步及完整三角指针网格遍历。[三角形求交](../assets/Shaders/gi_mesh_triangles.slang)调用已有 `IVertexEvaluate`，由材质解释顶点数据。GPU 上传时，三角指针转换为既有 Cluster 层级里的页引用地址，页驻留与搬移沿用引擎的维护结果。[场景查询](../assets/Shaders/gi_scene_tracing.slang)接收已有实例和分组范围，执行近景查询，以及 Global SDF 跳步、Object Grid 定位和对应区间的三角形查询。这些查询函数已通过独立 D3D12 软件设备的几何命中检查；帧管线的实际启用范围仍按下面的屏幕阶段说明。

DefaultGame 的 `prepare-mesh-fields [projectRoot]` 从已导入的几何生成该 Block 并保存回同一网格资产，沿用现有资产目录和存储。GPU 追踪的当前完成范围仍为屏幕阶段；近景 Mesh SDF、Global SDF、Object Grid、三角指针求交与 SHARC/NRC 的迁入遵循本页的完整主链。资产准备、屏幕阶段验证和完整 GI 验收分别按实际完成范围说明。

## 算法来源

外部软件 GI 原型位于 [generic-lod](F:/CodexProjects/2026-10-14/gi-lumen-sharc-nrc-surfal-3d/outputs/gi-validation-demo/generic-lod/AGENTS.md)。`staged_tracing.py` 与 `staged_tracing.wgsl` 持有逐级追踪，`screen_radiance_cache` 持有屏幕探针的方向辐射与像素积分；`screen_direct_lights.wgsl` 持有发光几何体直接光的采样与重建。

当前源码中的具体行为：

- `screen_trace.wgsl` 沿射线检查最多 20 个样点，投影到主表面深度，再对该像素的已有三角形求交。其查询长度当前与近景段共用 `NEAR_DISTANCE`；默认 `bounded` 模式继续确认屏幕候选之前的近景区间。
- 近景段按每条射线起点计，默认长度为 2 米。`distance_field.wgsl` 使用非负最近三角距离采样构造保守跳步，最多进行 8 次跳步，再由 `direct_cells.wgsl` 的局部三角引用列表确认命中；最多 4 个三角形的小网格直接求交。
- `staged_tracing.wgsl` 的 Global SDF 覆盖场景范围，处理近景段之后的射线区间。它在潜在表面区间调用 Object Grid 查询候选物体，再执行物体内的三角指针查询。
- `screen_radiance_cache.wgsl` 当前用解析平移和上一帧相机投影处理像素历史；探针历史通过旧三角编号与重心坐标重建世界位置，再分配旧探针编号。

这些行为分别说明当前原型的距离表示、屏幕查询范围和三角确认位置。给定射程内的抽样几何命中验证，与整套 GI 的覆盖、光照误差和性能评估分别进行。

当前原型的探针方向场合并反射间接光与天空直达。该混合信号用于对应的参考比较，分项计时按实际执行职责解释。
