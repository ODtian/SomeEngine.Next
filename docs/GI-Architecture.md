# GI 架构

本页记录用户确认的软件漫反射 GI 架构。2026-10-29 的修订确定：Global SDF 自身给出近似命中，随后查询 SHARC／NRC 的表面反射辐射；三角形求值集中在近景与稀疏训练。缓存内容、训练目标和查询结果采用同一光路范围。

## 逐级追踪

**屏幕空间追踪 → 近景 Mesh SDF → 后续射线段的 Global SDF。**

| 阶段 | 几何来源 | 结果与消费 |
| --- | --- | --- |
| 屏幕空间追踪 | 引擎深度、可见三角编号、材质提供的几何法线 | 确认当前可见表面，复用其反射辐射 |
| 近景 Mesh SDF | 网格局部距离采样、三角引用网格、现有几何页 | 距离跳步后对原三角求交，交给对应用户材质 Shader 解释 |
| Global SDF | 合成后的场景距离采样 | 重建近似命中位置与法线，直接查询 SHARC／NRC 的反射辐射 |
| 稀疏训练 | 原三角引用、用户顶点、域、可见性和表面 Shader | 生成缓存更新样本，包括该表面的材质与直接、间接光反射 |

近景按每条射线起点计，默认长度 2 米；GI 默认射程 24 米。屏幕空间候选给出距离上界，近景确认候选之前的区间。其余区间交给 Global SDF。

近景候选复用 Cluster 的实例包围盒、实例分组及顺序。三角指针指向既有 BVH 子项中的页引用，页缺失沿现有 Cluster 请求和驻留通路处理。Global SDF 的求交输入为距离场与射线，返回距离、位置和法线。

当前局部场保存最近三角距离。Global SDF 合成采样后的场景距离，按场分辨率确定表面厚度，通过距离梯度重建法线，并沿射线细化命中位置。距离场几何来自资产中的局部采样；实例的平移、旋转与缩放在合成时生效。

实现：[近景查询](../assets/Shaders/gi_mesh_distance_field.slang)、[用户顶点求值](../assets/Shaders/gi_mesh_triangles.slang)、[Global SDF 求交](../assets/Shaders/gi_scene_tracing.slang)、[场合成与逐级调度](../assets/Shaders/gi_pipeline.slang)。

## 反射辐射缓存

对次级命中表面 Y，缓存保存它向上一光路顶点返回的漫反射辐射。按引擎的漫反射积分约定：

```text
表面反射辐射(Y)
  = 材质漫反射反照率(Y)
    × [直接光入射积分(Y) + 间接光入射积分(Y)]
```

直接光包括解析光源、发光几何体采样及当前材质的环境光项。表面自身发光由对应的直达光路持有。主表面 X 直接看见发光表面 Y 的照明由发光几何体直接光处理；X 经 Y 反射看到发光表面 Z 的照明进入 Y 的反射辐射。

SHARC 与 NRC 共用 `GiRadianceSample`，其数据为位置、几何法线、反射辐射和有效性。SHARC 按位置、法线与尺度组织空间散列表；NRC 根据位置与法线预测同一种反射辐射。查询优先使用已有 SHARC 数据，再使用已开始有效更新的 NRC。缓存的神经网络更新次数独立于屏幕帧号维护。

稀疏训练先取得详细三角命中 Y，以用户材质求得反照率、法线、遮蔽和发光；再从 Y 采样续射，取得 Z 的反射辐射。Y 的直接光与续射贡献在更新端组合，生成完整反射辐射目标。发光几何体直达使用共用的重要性采样分布；训练续射保存后续反射项。屏幕上已经完成材质着色的反射辐射也作为更新样本。

Global SDF 命中后直接消费这个缓存结果。近景取得详细表面位置和法线，复用同一缓存；缓存建立期间由该表面的直接光提供初始反射值。

实现：[缓存及在线训练](../assets/Shaders/gi_radiance_cache.slang)、[训练光路](../assets/Shaders/gi_pipeline.slang)、[材质命中记录](../assets/Shaders/gi_material_queries.slang)。

## 屏幕空间探针

本页的屏幕空间探针指 Screen Probe：按屏幕 tile 布置、锚定在当前可见表面的采样点。它们发出的射线沿完整逐级追踪链查询场景。

每个 8×8 像素 tile 选择一个有效表面位置。失配像素通过 `GiProbeRequests` 请求覆盖，布置依次采用请求位置、tile 中心和 tile 内有效表面。探针保存双帧 SH9 方向入射辐射。

材质采用拉模式：以当前表面位置、几何法线、材质法线和漫反射参数查询上一帧屏幕空间探针，完成漫反射积分并合成颜色。同一次材质着色输出几何法线、材质法线、漫反射反照率和完整反射辐射，供当帧屏幕空间追踪及缓存训练消费。

运动对应使用材质已生成的运动向量。历史候选先核对几何法线、平面距离与空间范围，再对有效候选归一化。场景数据原点变化通过现有 `PreviousDataTranslation` 换算。首帧和相机切换由显式初始化 pass 重置屏幕历史，实际材质读取者直接依赖对应初始化 pass。

实现：[屏幕空间追踪及探针更新](../assets/Shaders/gi_screen_probes.slang)、[材质查询](../assets/Shaders/gi_probe_sampling.slang)、[材质输出](../assets/Shaders/gi_material.slang)、[运动向量](../wiki/architecture/features/Motion.md)。

## 直接光

太阳、点光源与聚光灯继续使用引擎 Lighting 和 VSM。标准材质与稀疏 GI 表面求值共用材质采样和方向无关的漫反射反照率。现有环境光项由共用的 `DiffuseEnvironmentIrradiance` 持有。

发光几何体直接光采用独立采样与重建。材质 Shader 提供发光强度上界及实际表面发光；GPU 按实例材质范围筛选发光几何，取得原三角位置、面积与通量分布。每个屏幕 tile 生成两个候选，像素从邻域候选重采样一个表面点，再按实际材质求值该点发光。直接光经过表面感知的空间过滤和运动重投影积累，由材质与间接光分别合成。

发光分布也供稀疏训练使用，直接光与缓存训练保持一致的发光表面解释。次级直接光的可见性查询采用当前 Global SDF。

实现：[发光几何体采样与重建](../assets/Shaders/gi_emissive_lights.slang)、[直接光材质查询](../assets/Shaders/gi_direct_sampling.slang)、[标准材质](../assets/Shaders/standard_material.slang)。

## 数据持有与引擎接入

数据进入路径为 **Game world → extraction → Rendering world → 渲染资源与 pass**。

`MeshDistanceFieldBlock` 与同一 MeshAsset 中的 Cluster 几何共用资产身份。导入器生产局部距离采样、三角网格和原三角指针，加载方创建 `MeshDistanceField` 使用对象。`MeshInstance.DistanceField` 通过已有 `MeshRenderExtractor` 交付 `RenderMesh.DistanceField`，同一网格的实例共享同一数据引用。

`GiSceneData` 消费 Rendering world 的 `RenderMesh` 和 `RenderClusterGeometry`。它持有 GPU 距离场数据及按现有 `RootBvh` 索引的 `uint2`：距离场字节地址、材质绑定范围数。三角指针按已有驻留分配重定位；原始几何页、BVH、实例、材质标量和纹理仍由对应引擎设施持有。

近景与训练的候选按既有材质编号分桶。`IntersectGiMaterial` 和 `ShadeGiMaterial` 调用用户的 `IVertexEvaluate`、`IDomainEvaluate`、可见性解释与 `IGiSurfaceEvaluate`。Cluster 的材质 compute 接入复用同一 Shader 管线缓存、标量区、纹理输入和图资源绑定。

固定 pass 与长期缓冲通过持久 RenderGraph 建立。场内容随实例版本或网格字段变化更新；当帧射线、候选、分桶与训练样本通过已有 pass 更新。几何页和 BVH 访问使用引擎现有的实际驻留范围。GI 页请求在既有请求完成 pass 之前交付，随后沿同一反馈通路进入流送。

入口：

- [ScreenProbeFeature](../src/SomeEngine.Render.Features/GI/ScreenProbeFeature.cs)：GI 资源、缓存与完整帧链。
- [GiSceneData](../src/SomeEngine.Render.Features/GI/GiSceneData.cs)：网格字段及根编号索引。
- [材质 compute 接入](../src/SomeEngine.Render.Cluster.Pipeline/ClusterPipelineSystem.MaterialCompute.cs)：复用现有材质和几何资源。
- [DefaultGame 接入](../samples/DefaultGame/ExperimentalGi.cs)：通过 `--experimental-gi` 或场景面板选择 GI。

## 帧顺序

1. 几何准备与图协调：处理网格字段及材质变化，维护已有资源和 pass。
2. 初始化历史后，主表面材质读取上一帧屏幕空间探针及直接光历史，并输出当前表面数据。
3. 按变化更新 Global SDF，生成屏幕空间探针射线及稀疏训练射线。
4. 近景和训练候选按材质分桶求交、选取最近命中，再按材质求表面数据。
5. 普通射线完成屏幕、近景与 Global SDF 的逐级选择，取得反射辐射；稀疏训练继续追踪并生成完整反射辐射样本。
6. 更新 SHARC 与 NRC，积累当帧屏幕空间探针方向辐射。
7. 更新发光几何体分布，采样、重建并积累独立直接光结果，供下一帧材质查询。
8. 完成共享页请求和当帧输出。

## 验证与来源

公共行为检查覆盖近景三角命中、Global SDF 的位置／法线／射程，以及反射辐射缓存的更新与查询。引擎画面沿 DefaultGame 公共入口检查；统一场景平台使用 1024 小球、Sponza、Amazon Bistro。测试和测量条件及结果在当前会话汇报，规范见 [行为检查](../wiki/harness/Behavior-Checks.md)与[性能测量](../wiki/harness/Harness-Performance-Evidence.md)。

算法来源为外部 [软件 GI 原型](F:/CodexProjects/2026-10-14/gi-lumen-sharc-nrc-surfal-3d/outputs/gi-validation-demo/generic-lod/AGENTS.md)。原型中的 Global SDF 曾通过 Object Grid 回查三角；本页采用上述用户确认的距离场终点与反射辐射缓存职责。
