# GI 架构

本页以用户最初确认的迁移任务为准：把软件 GI 原型的整条管线接入引擎，保留其阶段和采样算法；Global SDF 自身确定命中并查询 SHARC／NRC 的完整漫反射辐射。缓存内容、训练目标和消费范围采用同一量纲。

原型入口为 [generic-lod](F:/CodexProjects/2026-10-14/gi-lumen-sharc-nrc-surfal-3d/outputs/gi-validation-demo/generic-lod/AGENTS.md)。实际 `screen_grid_ray` 配置由 `screen_probes.py` 转入 `screen_radiance_cache.py`，该文件的 `encode` 持有本页对应的执行顺序。

## 追踪与反射辐射

**屏幕空间追踪 → 近景 Mesh SDF → 后续射线段的 Global SDF。**

| 阶段 | 几何与命中数据 | 光照消费 |
| --- | --- | --- |
| 屏幕空间追踪 | 主表面的深度、可见三角编号、用户材质给出的几何法线 | 命中表面的完整反射辐射，结合反射辐射缓存与像素历史复用 |
| 近景 Mesh SDF | 每个网格的距离采样，默认处理射线起点后的 2 米 | SDF 保守跳步后查询局部三角指针，由用户的顶点、域和可见性求值器确认表面 |
| Global SDF | 合成后的距离采样，重建近似位置、距离与法线 | 直接以命中位置、法线和足迹查询 SHARC／NRC 的反射辐射 |

`CollectGiMeshQueries` 把几何任务组织为射线、实例和用户材质。`IntersectGiMaterial` 在用户 Shader 内执行近景距离场查询，三角求交立即更新本次查询和该射线的最近距离，供后续局部遍历剪枝。主视图的近景 tile 候选负责剔除物体。

距离场加载时通过同一份 `MeshPayloadSource` 读取打包页中的三角材质区域，生成原三角指针对应的局部材质编号，并合并原始簇的材质包围范围。近景任务入队前先用该范围裁剪材质。每个终端网格单元的指针按材质连续排列，同材质内保持原有顺序。用户材质查询通过二分定位自己的连续区间，再沿 SDF 跳步和最近命中反馈执行求交；多材质网格共享同一份距离场和页资源。

Gather 和训练续射共同消费 `ResolveGiRayRadiance`。两者的材质几何任务都截在近景区间；远段终点统一为 `TraceGiGlobalScene → QueryGiReflectedRadiance`。天空辐射提供未命中方向的入射边界。

实现：[距离场与局部三角指针](../assets/Shaders/gi_mesh_distance_field.slang)、[用户材质求交](../assets/Shaders/gi_material_entries.slang)、[统一追踪与训练](../assets/Shaders/gi_pipeline.slang)、[Global SDF 命中](../assets/Shaders/gi_scene_tracing.slang)。

## 两个 World 与数据持有

Game world 的网格引用通过既有提取进入 Rendering world。`RenderMesh.DistanceField` 保存该网格共享的距离场对象，`GiSceneData` 使用现有 Cluster root 建立地址索引，并直接写入 RenderGraph 的上传页。三角指针指向既有层级中的页引用；页驻留、请求、搬移和回收沿用 Cluster 的同一份资源。

指针重定位读取网格持有的不可变数据，并向上传页写入最终地址。应用通过 `ApplicationRendering.Lights` 把 Rendering world 的同一份 `RenderLightSet` 交给 GI，其版本驱动辐射历史刷新。GI 批量接入材质 pass 后调用一次 `ReconcileMaterialResources`，由 Cluster 统一清理输入并登记缓冲和纹理。

所有材质编号共用一份持久常量缓冲，各材质 pass 读取自己的对齐区间。容量及 GI 缓冲重置驱动上传，帧内复用既有数据与绑定。

顶点字节、域、可见性和表面属性由用户自己的材质 Shader 解释。`IGiSurfaceEvaluate` 给出法线、几何法线、漫反射率、覆盖率、环境遮蔽与发光。主材质在既有着色阶段输出这些主表面数据及解析直接光，GI 随后完成本帧方向辐射重建与颜色积分。

全局场采用静态场和当前场两份采样。实例运动由同一份 current／previous transform 派生；静态部分在几何、成员或覆盖范围变化时维护，当帧从静态场复制并合成运动部分。

实现：[渲染场景距离数据](../src/SomeEngine.Render.Features/GI/GiSceneData.cs)、[GI 资源与更新](../src/SomeEngine.Render.Features/GI/ScreenProbeFeature.cs)、[两个 World](../wiki/architecture/rendering/GameWorld-and-RenderWorld.md)。

## 屏幕空间探针

本框架的方向辐射探针是屏幕空间探针。它们保存接收表面的方向入射辐射；SHARC／NRC 保存次级表面的反射辐射，两者依次承担光路复用与接收面积分。

布局沿用原型的 16×16 像素 tile，每个 tile 分配 0–4 个探针。几何法线和相对平面分布决定数量，前缀和给出双帧探针区间，误差最大的表面点补充后续锚点。覆盖请求选择下一帧 tile 的最后一个探针。

重投影使用引擎的 previous／current transform、视图和运动向量，核对实例、位置、表面朝向与 tile。静态探针累计到 64 个样本后保存已有 SH；辐射变化时历史上限为 8，刷新按原型的 2 帧节奏分散。每个活动探针发射一条 R2 方向射线。

原始 SH9 先累计本帧入射样本，再读取 3×3 tile 中最多 36 个邻近锚点做几何匹配与空间滤波。有效样本数决定高阶方向系数向低频先验的混合。像素从相邻四个 tile 的全部锚点中选择权重最高的四个，使用本帧过滤 SH 和用户材质法线完成漫反射积分。

实现：[布局与分配](../assets/Shaders/gi_probe_layout.slang)、[重投影与 SH 滤波](../assets/Shaders/gi_probe_history.slang)、[射线与 SH 累计](../assets/Shaders/gi_screen_probes.slang)、[本帧像素积分](../assets/Shaders/gi_pixel_radiance.slang)。

## 缓存与训练目标

缓存保存表面向外反射的漫反射辐射。直接光、环境和后续反射传播共同形成该值；发光表面的自发光通过直接光采样处理。

训练每四帧从活动探针的分层区间旋转选样，单批最多 128 个样本。接收面样本与已知次级表面样本的选择独立于该方向落入哪一级追踪，以保持接收面半球估计的采样分布。

- 已知主表面使用当前直接反射辐射和 gather 的入射样本形成完整反射目标。均匀半球采样的漫反射权重为 `2 × max(dot(materialNormal, rayDirection), 0)`。
- 已知近景或屏幕次级表面使用余弦续射，目标为 `DirectReflected(Y) + Diffuse(Y) × ContinuationIncoming(Y)`。几何面下方的方向贡献归零，续射的远段直接查询 Global SDF 命中的反射辐射缓存。

有效训练批次驱动 GPU 间接派发和神经更新次数。Probe shading 消费更新前的缓存；本帧探针累计与滤波完成后再更新训练缓存。

SHARC 使用 65,536 个条目、八个空间尺度和六轴法线分组，查询成熟单元及邻近成熟单元；静态与变化历史分别采用原型的 8 和 2。NRC 保留原型的 24→32 ReLU→3 网络、899 个权重、相对辐射误差与 Adam 更新。两者消费相同的完整反射目标。

实现：[训练与查询](../assets/Shaders/gi_pipeline.slang)、[SHARC／NRC](../assets/Shaders/gi_radiance_cache.slang)。

## 发光几何体直接光

用户 Shader 计算发光范围、三角位置和采样点发光。发光物体树与物体内三角树使用包围范围、通量和接收面的方向关系选择候选。三角采样沿用原型的球面三角采样与面积采样切换。

每个 8×8 像素区域共享两个物体候选；像素汇集邻区候选并重采样物体，再为该像素采样三角上的光点。对应用户材质解释光点的实际覆盖与发光。静态与运动光照分开滤波和累计，像素几何历史由 `GiPixelSurfaces` 统一持有。

发光拓扑持久保存；实例、材料发光或几何页可用性变化触发相应维护。发光分布在探针与训练消费之前准备。

实现：[重要性树与三角采样](../assets/Shaders/gi_emissive_sampling.slang)、[候选、材质光点与重建](../assets/Shaders/gi_emissive_lights.slang)、[资源与拓扑](../src/SomeEngine.Render.Features/GI/ScreenProbeFeature.Emissive.cs)。

## 本帧执行顺序

```text
主几何与直接光表面输出
  → 静态／当前 Global SDF 与发光分布维护
  → 探针布局、锚点重投影、刷新预算
  → Screen Trace、近景 Mesh SDF、Global SDF 缓存查询
  → 原始 SH 累计、空间滤波
  → 活动探针训练选样、续射、SHARC／NRC 更新
  → 发光直接光候选、像素重采样、用户材质光点求值、空间滤波
  → 本帧 SH 的像素积分与三类光照历史累计
  → 材质颜色、TAA、呈现
```

主体图保持持久拓扑。网格、材质及容量变化通过显式编辑维护；帧内更新常量、资源内容、启用区域和间接工作量。DefaultGame 通过 `ExperimentalGi` 选择资产并安装引擎功能。

探针过滤完成是主线的阶段边界。训练支链持续保留，每四帧生成一批有效样本，其余帧由空工作量贯穿；有效训练样本决定 SHARC／NRC 的间接派发和网络更新次数。发光直接光等待该支链完成，随后复用同一份射线与材质几何工作区。

## Linux 与测量入口

Linux 使用 Xlib 窗口和 Vulkan Surface，窗口句柄与 display 通过现有 `SurfaceDesc` 传递。开发资产在 Linux 构建 SPIRV，发布沿用 Release NativeAOT。

Linux 窗口与呈现复用引擎优化树的 `97277eb9` 适配，图形后端复用该树的 `5535b59f`，包含紧凑绑定坐标、管线编译数据复用与时间戳队列选择。Shader 特化使用 `8199bba8` 的驻留输入恢复方式。`--measure` 自动启动统一场景平台并在结果完成后退出；`tools/Measure-Scenes.ps1` 沿用该树的 Python 执行器与 Xorg 入口，为队列选择的 GPU 创建普通呈现窗口。性能运行通过共享队列在服务器执行 1024 小球、Sponza 和 Amazon Bistro。比较使用相同分辨率、场景行为、渲染选项、NativeAOT 和采样窗口。

`--gpu-times` 按平台实际采样的帧归档 GPU 命令时间与包含屏障、等待和提交间隙的整帧跨度。定位运行与普通性能运行分别记录。
