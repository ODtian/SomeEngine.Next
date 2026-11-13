# VSM 与 tile 渲染重构方案（2026-11-08）

本方案解决审查中在 VSM、光照网格、透明 OIT、有序合成、贴花、材质光栅分箱和材质协议上发现的全部问题。每个工作项的写法与 [Render-Refactor-Plan-2026-11-08](Render-Refactor-Plan-2026-11-08.md) 相同：

- **现状**：已在源码核对的事实，附位置。
- **改完后**：类型、字段归属、签名、删除清单。
- **判定**：用 ARCH（架构检查）、BEH（行为检查）、PERF（性能证据）判断是否改到位。全部满足才算完成。

ARCH 检查的两种实现方式（元数据检查、源码语法树检查）和放置位置沿用上一份方案第 1 节，本文从 ARCH-23 起编号。

## 0. 与上一份方案的关系

| 上一份方案中的项 | 本文的处理 |
|---|---|
| F2 拆分表中的 `TransparencyFeature`、`MaterialDecalFeature`、`BlendMeasurementFeature` 三行 | 作废。透明和有序合成由本文 T 组的 `LayerComposition` 承担；贴花由 DE 组的 `MaterialDecalBinning` 承担；混合测量随旧透明实现一起删除 |
| F4 中"`TransparencyFeature` 自己声明 tile 网格"一条 | 作废，由 T1、DE1 取代 |
| F1 的 `ViewTargetKind { Screen, ShadowPages }` | 扩展为 `{ Screen, ShadowPages, LayerTiles }`（T2） |
| F3 合并 VSM | 保持不变。本文 V 组的改动都落在合并后的类型上，类型名以 F3 为准 |
| C1 帧提交语义、C3 资源状态归属 | 是本文的前提。本文涉及的所有 feature 都遵守"暂存不改字段、提交时才改"和"资源只由 RG 创建" |

依赖：本文 T、M 组依赖上一份方案的 C1、C3、F1、F2、B2；V 组依赖 F3。

## 1. 需要先拍板的决策

| 编号 | 决策 |
|---|---|
| D7 | 透明、有序合成、X-ray 全部改由 `LayerComposition` 承担。删除 Full/XY/XYZ 三种候选索引选项，删除近似终止（`MaximumError`、`ColorLimit`）。合成精确：每个像素的全部片元按片元深度排序后依次合成，层数不设上限，没有候选槽位和轮次。唯一的限制是片元存储容量：不够时报告溢出，在读回送达那一帧的 prepare 阶段扩容。 |
| D8 | 保留材质完整的 `evaluateSurface` 程序，将写出目标改为该方法的类型参数。同一份材质实现由不透明 pass 传入屏幕颜色目标、层 pass 传入片元载荷目标。采样、光照、阴影采样、AO、贴花和材质属性解释继续由材质完成；引擎资源声明归引擎模块，材质通过 import 使用库。 |
| D9 | 层接收者的 VSM 页请求在层可见性光栅中就地标记：每个通过可见性测试和不透明深度测试的片元都会参与合成，所以逐片元标记是精确的，并且全部层在当帧 VSM 分配之前完成请求。不透明的请求仍从深度生成，因为不透明光栅存在被覆盖的片元。没有延迟请求。 |
| D10 | 贴花应用保留在材质中，材质继续调用 `BeginMaterialDecals` / `NextMaterialDecal` 等库函数并解释数据。DE1 负责贴花独立网格与分箱，沿用现有 coating 等材质库行为。 |
| D11 | VSM 缓存按灯光身份（灯光实体加面或 clipmap 级别）识别，页表按灯光分配稳定区段。 |
| D12 | 方向光启用 receiver mask 时，动态层每帧重画这一行为改为设置项 `DirectionalDynamicReceiverRefresh`。默认值由 PERF-V10 的测量结果决定，结论写进 Virtual-Shadow-Maps 页。 |
| D13 | 层片元记录保存完整的 32 位深度、片元身份、tile 内像素下标、pass 序号和 `DrawOrder`（格式见 T4）。记录不做原子插入，所以不再受 64 位键的位宽限制。`MaterialLayer.DrawOrder` 使用 `byte`。着色从片元身份恢复原始三角形，用记录中的深度反投影目标位置，交给用户 `Domain.reconstruct` 反算重心坐标与导数。曲面的反算由用户 Domain 负责。 |
| D14 | 保存图编辑结果的 feature 自持 `BeginEdit → 改图 → Commit`，提交成功后采用 pass、资源 ID、注册次序和容量。无状态辅助接收 `ref RenderGraphEdit` 并只返回图 ID 值，由调用方在提交后保存。通用规则见 [Graph-Edits](../wiki/architecture/rendering/Graph-Edits.md)。 |
| D15 | 基础渲染能力提供 VS/PS 与 compute 路径，覆盖多材质、不透明和细分。缺少 A64 时提供全硬件光栅和非原子直接写入两条管线 fallback；选择和写出语义见 [Raster-Execution](../wiki/architecture/cluster/Raster-Execution.md)。 |

## 2. 本文新增的架构检查

| 编号 | 类型 | 规则（违反即失败） |
|---|---|---|
| ARCH-23 | 源码 | `assets/Shaders/` 中不出现标识符 `TargetPolicy` 和 `targetPolicy`。光栅输出由泛型类型参数选择。 |
| ARCH-24 | 源码 | 材质 shader 文件（声明了实现 `IMaterial` 或 `ISurfaceEvaluate` 类型的文件，以及 `samples/**/assets/Shaders/` 下的材质文件）不包含：`#include "cluster_`、`#define CLUSTER_`、`render_lighting`、`vsm_`、`material_decals`、`TRANSPARENCY_MATERIAL_TYPE`、固定目标 `outputColor`，并且引擎资源的直接声明归引擎模块。材质通过 import 使用公开光照、阴影采样和贴花库；底层资源和请求模块由引擎库封装，页请求由 M3 执行。入口在烘焙时生成。 |
| ARCH-25 | 源码 | 全仓库不出现 `ITransparentSurfaceEvaluate`、`markShadowPages`、`evaluateTransparency`。`MarkVirtualShadowPages` 只出现在 VSM 的请求库模块中，调用方只有 VSM 自己的深度请求 pass 和 `LayerVisibilityOutput`。 |
| ARCH-26 | 元数据 | 退役公开 `Material.State`、`SomeEngine.Resources.MaterialState`、`SurfaceMode`。材质通过自己的 shader、属性和 `MaterialLayer` 表达行为；光栅面由烘焙 `RasterFaces` 指定，可见性和深度由用户 Visibility 实现。 |
| ARCH-27 | 文件系统 | 以下文件不存在：`assets/Shaders/cluster_transparency*.slang`、`assets/Shaders/material_decal_cells.slang`、`src/SomeEngine.Render.Cluster.Pipeline/ClusterPipelineSystem.{Transparency,TransparencyShaders,Decals,Ordered,BlendMeasurements}.cs`、`ClusterStencil.cs`、`ClusterBlend*.cs`。`ClusterPipelineOptions` 不含名字以 `Blend` 开头的成员。 |
| ARCH-28 | 源码 | 光照网格、层 tile 列表、贴花分箱、VSM 投影物失效这四类 shader 都引用共同的 `screen_binning` 模块（按 B9 采用 `import`，宏配置源采用 `#include "screen_binning.slang"`）。列表偏移只来自 `ScreenBinningScan`，不出现名字以 `Reserve` 开头的函数。 |
| ARCH-29 | 源码 | 上一条中的四类 shader 里，单个线程不循环遍历 cell 矩形或页矩形，即不出现以 `Minimum`/`Maximum`/`low`/`high` 为上下界的二维或三维 `for` 循环。矩形一律通过 `ScreenBinningExpand` 展开。 |
| ARCH-30 | 源码 | `src/SomeEngine.Render.Features/Lighting/` 和 `clustered_light_grid.slang` 中不出现 `SinglePoint`、`GroupsPerLight`、`-v2`。 |
| ARCH-31 | 源码 | VSM 代码中不出现：`viewLayoutChanged`、`_forceStateReset`、`_previousForceStateReset`、`ReferenceEquals(_frameInstances`。`PrepareViews` 没有 `geometryRevision`、`rasterRevision`、`instanceCount` 参数。`VirtualShadowPageMetadata` 没有 `ViewIndex` 字段。 |
| ARCH-32 | 源码 | VSM shader 中不出现 `VSM_PAGE_LOCKED`、`VSM_PAGE_RECEIVER_DEPTH`。 |
| ARCH-33 | 源码 | VSM 的 `StageFrame`、`PrepareViews`、`SelectDistantUpdates`、`OnFrameSubmitted` 方法体内，不出现 `new` 数组或集合、LINQ 调用（`Select`、`Where`、`ToArray`、`ToList`）、`graph.AddCopyPass`、`graph.CreateBuffer`。 |
| ARCH-34 | 源码 | `cluster_bin_reserve.slang` 不在溢出时把 `BinSWCount` 或 `BinHWCount` 写为 0，存在名为 `RasterSpillClusters` 的资源。 |
| ARCH-35 | 源码与元数据 | `SomeEngine.Render.Features/Layers/`、`SomeEngine.Render.Features/Decals/` 以及对应的 shader 不引用 `ClusteredLightGridParameters`、`LightingFeature`、`LightingViews`。 |
| ARCH-36 | 源码 | `assets/Shaders/` 中，材质库以外的引擎 shader 不出现 `BaseColor`、`Roughness`、`Metallic`、`Occlusion` 等材质表面属性标识符。标准材质、coating 辅助及 BRDF 等材质库按自身模型解释这些数据。pass 输出与累加器只规定该 pass 消费的数据。 |
| ARCH-37 | 元数据 | `LayerComposition` 的公开成员接收已提交资源或描述数据，参数中不出现 `RenderGraphEdit`；`ScreenBinningPasses` 没有实例字段。 |
| ARCH-38 | 源码 | `src/` 和 `assets/Shaders/` 中不出现：`LayerSortTileLists`、`LayerRetireClusters`、`CandidatesPerRound`、`MaximumRounds`、`CandidateBudget`、`IncompletePixels`、`LayerDeferredPageRequests`、`LayerCandidateOutput`、`LayerPrepareRound`。 |
| ARCH-39 | 源码 | `assets/Shaders/` 中层合成的 shader（`layer_*.slang`）不 import 任何材质模块，也不调用 `evaluateSurface`。层片元的着色只经由 `shade_surface_entries.slang` 的 `ShadeSurface<M, LayerFragmentInput, LayerPayloadOutput>` 特化。 |

## 3. 共享分箱工具（B 组）

### B1 `ScreenBinning`

**现状**
- 同一件事有三套实现：光照网格用链表加压缩，透明和贴花用计数、预留、写入。
- 预留用原子计数器，放不下的 cell 照样推进计数器，导致之后的 cell 连锁溢出：`ReserveTransparency`（[cluster_transparency.slang](../assets/Shaders/cluster_transparency.slang)）、`ReserveMaterialDecalCells`（[material_decal_cells.slang](../assets/Shaders/material_decal_cells.slang)）。
- 审查时大范围的条目由单个线程三重循环逐 cell 写入：透明三角形、贴花体积、VSM 投影物失效。投影物的现行展开入口见 V5。
- 光照的 cell 内顺序由原子链表决定，每帧不同。

**改完后**
- shader 模块 `assets/Shaders/screen_binning.slang` 提供五步，每步都是普通的 compute 入口函数，由使用方的 shader 以自己的条目类型特化：
  1. `ScreenBinningCount<TItem>`：每个条目算出它覆盖的矩形（cell 或页），写出覆盖数量。
  2. `ScreenBinningScan`：对覆盖数量做多工作组前缀和，得到每个条目在展开空间中的起点。
  3. `ScreenBinningExpand<TItem>`：每个（条目, 矩形内位置）一个线程，二分查找所属条目后执行使用方的测试和写入。每个 cell 的计数在这一步累加。
  4. `ScreenBinningScanCells`：对每个 cell 的计数做前缀和，得到 cell 列表的起点，偏移完全确定。
  5. `ScreenBinningFill`：再展开一次，用"cell 起点 + 条目在 cell 内的序号"写入。cell 内序号来自第 3 步按条目下标稳定排序的结果，所以 cell 内顺序等于条目下标顺序。
- 容量处理：
  - 第 4 步算出总长度；超过容量时，按 cell 下标顺序完整保留前面的 cell，剩余 cell 标记为溢出，并通过公共诊断报告溢出数量。不存在连锁溢出。
  - 读回送达的那一帧（帧 N 的资源按 F 帧在途复用时为 N+F），通过使用方自己的 prepare 系统显式编辑扩容（`SetBufferDescription`），提交成功后采用容量。
- C# 侧静态类型 `SomeEngine.Render.ScreenBinningPasses`：在调用方传入的 `RenderGraphEdit` 中注册上述 5 个持久 pass 和缓冲，参数为条目容量、cell 数、列表容量，返回由图 ID 组成的值。辅助类型保持无实例字段；使用方提交编辑后保存返回值并持有诊断反馈。计算管线来自已有 `RenderPipelines`。

**判定**
- ARCH-28、ARCH-29 通过。
- BEH-B1a：用公共 API 向一个测试使用方提交 1 个覆盖 80×45×16 个 cell 的条目，读回的 cell 列表中每个 cell 恰好包含这个条目。
- BEH-B1b：提交多个条目，读回的每个 cell 列表按条目下标升序；连续两帧的读回结果逐字节相同。
- BEH-B1c：把列表容量设为总需求的一半，读回结果中，下标较小的 cell 完整、其余 cell 为空，公共诊断报告的溢出 cell 数等于空 cell 数。

### B2 材质光栅分箱不再整箱丢弃

**现状**
- `ReserveBins` 在分箱放不下时把这个分箱的 `BinSWCount` 和 `BinHWCount` 写为 0，整个材质当帧消失，消失的是哪个材质取决于原子竞争（[cluster_bin_reserve.slang](../assets/Shaders/cluster_bin_reserve.slang)）。

**改完后**
- 分箱的批次预留改为对各分箱数量做前缀和（`ScreenBinningScan`），按分箱下标顺序能放多少放多少。
- 放不下的 cluster 写进 `RasterSpillClusters`（每项一个可见 cluster 下标，容量等于 `MaxVisibleClusters`，所以一定放得下）。
- 每个光栅阶段的入口多一个输入特化 `RasterInput.SpilledClusters`：直接读 cluster，在三角形级别按材质过滤。烘焙期生成，与普通的批次输入特化并存。
- 同一帧内对溢出的 cluster 用同一条材质管线再派发一次，结果完整。溢出数量通过 `ClusterWorkMetrics.Overflow` 报告，读回送达那一帧（N+F）的 prepare 阶段提交扩容。

**判定**
- ARCH-34 通过。
- BEH-B2：把批次容量设为需求的四分之一，渲染图像与容量充足时在指定区域内一致，`ClusterWorkMetrics.Overflow` 带有光栅批次溢出标志。

## 4. 光照网格（L 组）

### L1 逐 cell 相交测试

**现状**
- 灯光被分配到它整个屏幕包围矩形乘以深度切片范围，不做逐 cell 测试。离相机近的点光会覆盖整屏的所有切片（[clustered_light_grid.slang](../assets/Shaders/clustered_light_grid.slang) 的 `GetClusteredLightCellBounds`、`AssignClusteredLightCell`）。

**改完后**
- 光照网格成为 `ScreenBinning` 的使用方。条目是非方向光，矩形由现有的包围计算给出。
- `ScreenBinningExpand` 中调用新函数 `ClusteredLightIntersectsCell(light, cell)`：
  - 点光：球体与 cell 的视空间包围盒相交；
  - spot 光：先做球体测试，再做圆锥与包围盒的保守测试。
- 删除 `BuildClusteredLightGrid`、`CompactClusteredLightGrid`、`LightLinkList`、`LightAllocationCounters`、`AllocateClusteredLightLink`。

**判定**
- ARCH-28、ARCH-29 通过。
- BEH-L1：一个半径 1 米、位于相机前 2 米的点光，读回网格中包含它的 cell 集合，等于测试用公开网格参数在 CPU 上算出的"包围盒与球体相交的 cell"集合。

### L2 cell 内顺序确定

**改完后**
- 由 B1 第 5 步保证：每个 cell 的灯光下标升序。
- CPU 上传灯光时按优先级排序，规则写在 `RenderLightSet`：投影灯在前，然后按"强度除以到相机距离的平方"降序，相等时按实体 id。灯光下标顺序即优先级顺序。

**判定**
- BEH-L2：同一场景连续渲染两帧，读回的 `LightIndexList` 逐字节相同，并且每个 cell 段内升序。

### L3 容量语义

**现状**
- `MaxLightsPerCell` 只用来估算全局容量（cell 数 × 64），并不限制单个 cell；超出全局容量时随机丢灯。

**改完后**
- `ClusteredLightGridSettings` 中删除 `MaxLightsPerCell`，改为两个设置：
  - `LightListCapacity`：总容量。
  - `MaxLightsPerCell`：真正的每 cell 上限。超出时保留下标最小（即优先级最高）的若干盏，写入发生在 B1 第 5 步，按 cell 内序号截断。
- 截断或总容量溢出时，`VirtualShadowFrameQuality.LightAssignmentOverflow` 和新增的 `LightingFeature.LastFrameOverflowCells` 报告数量。
- feature 自己的 prepare 系统在读回送达那一帧（N+F）提交总容量扩容，位置在 `TryOpenNextFrame` 之后、`BeginFrame(next)` 之前。

**判定**
- BEH-L3：一个 cell 内放 `MaxLightsPerCell + 3` 盏灯，读回的列表恰好是下标最小的 `MaxLightsPerCell` 盏，`LastFrameOverflowCells` 大于 0。

### L4 删除单点光专用路径

**改完后**
- 删除 `PrepareSinglePointLightGrid`、`FillSinglePointLightGrid`、`ClusteredSinglePointDirectAttribute`，以及 `LightingFeature` 中的 `_singlePoint*` 字段和 `HasSinglePointRole` 检测。

**判定**
- ARCH-30 通过。
- PERF-L4：1024 小球场景（单盏点光）的帧时间与 A2 基线对比，在同一份报告中给出。

## 5. 贴花（DE 组）

### DE1 `MaterialDecalBinning`

**现状**
- 贴花的 cell 网格借用光照网格（`MaterialDecalLightingViews`），预留会连锁溢出，单线程循环遍历 cell。
- 贴花由接收方材质在自己的表面求值里遍历应用（[standard_material.slang:133](../assets/Shaders/standard_material.slang)）。

**改完后**
- 新类型 `SomeEngine.Render.Features/Decals/MaterialDecalBinning`，拥有自己的 froxel 参数（tile 尺寸、深度切片数），是 `ScreenBinning` 的使用方。条目是贴花体积，覆盖测试为盒子与 cell 包围盒相交。cell 内按贴花作者次序排列：条目下标按作者次序上传，由 B1 保证顺序。
- 材质继续调用 `BeginMaterialDecals` / `NextMaterialDecal`，以当前像素位置和导数取得贴花用户数据与投影坐标，再由材质解释和混合。库内部改为读取该独立网格；现有 `EvaluateCoating` 采样与混合行为沿用。
- 删除 `material_decal_cells.slang`、`ClusterPipelineSystem.Decals.cs`。

**判定**
- ARCH-27、ARCH-28、ARCH-35 通过。
- 现有的 `decal-layers`、`decal-sparse` 效果图像与改动前在指定区域内一致。
- BEH-DE1：16 层重叠贴花，倒序提交实体后渲染图像逐字节相同。

## 6. 材质协议（M 组）

### M1 材质单份着色程序使用泛型写出目标

**现状**
- `ISurfaceEvaluate.evaluateSurface` 返回 `void`，直接把最终颜色写进 `RWTexture2DArray<float4> outputColor`（[material_interfaces.slang](../assets/Shaders/material_interfaces.slang)）。
- `CSShadeCore` 从 bin 取像素、解码三角形、重建重心坐标并写运动向量，随后调用材质；材质调用后完成该像素的着色（[cluster_shade_pipeline.slang](../assets/Shaders/cluster_shade_pipeline.slang)）。
- 标准材质自己采样、遍历贴花和灯光、做 VSM 采样并计算 AO（[standard_material.slang](../assets/Shaders/standard_material.slang)）。这已经是材质完整执行自身着色的模型。
- `ITransparentSurfaceEvaluate` 要求材质实现 `evaluateTransparency` 和 `markShadowPages`。
- 工作区已有 include → import 的模块化改动，`material_interfaces.slang`、`standard_material.slang` 与入口源码已采用部分 import；引擎资源声明的归属沿该改动核对并接入。

**改完后**
- `IMaterial`（在 `material_interfaces.slang` 中）用 Slang 的 associatedtype 为烘焙入口声明几何组合：
  - `associatedtype Vertex : IVertexEvaluate`
  - `associatedtype Domain : IDomainEvaluate`
  - `associatedtype Visibility : IVisibilityEvaluate`
- 保留 `ISurfaceEvaluate<TSurface, TTriangle>` 的完整材质行为，将 `evaluateSurface` 和相应调试写出的目标改为方法类型参数 `TOutput`。`SurfacePixelInput`、用户的曲面和三角形类型、重心坐标与导数沿用现有强类型链路。
- 不透明 pass 传入屏幕颜色目标，层 pass 传入片元载荷目标（写到该片元记录对应的载荷位置）；目标适配器解释写出操作。同一材质只维护一份 `evaluateSurface`，由编译期特化选择目标。
- 采样、BRDF、AO、光照循环、阴影采样、背面处理和贴花应用由材质继续完成。标准材质的 `baseColor * armSample.r * 0.03` 环境项保持其现有含义。
- `LightingViews`、`LightBuffer`、`MaterialViews`、`MaterialDepth` 等引擎资源声明迁入引擎模块，材质 import 并调用库；资源名到 RG 资源的映射保持引擎持有。
- 删除 `ITransparentSurfaceEvaluate`、`evaluateTransparency` 和材质内的 `markShadowPages`。页请求由 M3 承担。
- 引擎继续提供可见性调试，材质保留自身属性调试。调试模式统一由已烘焙的 `ClusterShadeMode<debugMode>` 特化选择，`CSShadeCore` 通过相同编译期参数调用对应材质路径。
- `standard_material.slang`、`unlit_material.slang`、地形材质、`ImportedScenes` 材质和 `samples/DefaultGame` 的材质迁移到泛型目标调用与烘焙入口组合。

**判定**
- ARCH-24、ARCH-25、ARCH-36 通过。
- 现有全部图像测试（Cluster 图像检查、VSM 图像、场景图像）通过，与改动前在指定区域内一致。

### M2 入口由烘焙生成，目标由类型参数选择

**现状**
- 材质文件是管线的组合根：要定义四个固定名字的 typedef、一组 `CLUSTER_*` 宏，并 include 管线入口文件和生成的实例布局（[standard.slang](../samples/DefaultGame/assets/Shaders/standard.slang)、[deformed-phong.slang](../samples/DefaultGame/assets/Shaders/deformed-phong.slang)）。透明还要再写一个 `_transparency.slang`。
- 当前 `SlangShaderImporter` 位于 `src/SomeEngine.Assets/Importing/Shaders/`；入口导入、特化声明和共享 IR 分别由该目录内 partial 实现、`ShaderSpecialization` 与 `ShaderSourceBlock` 持有，接入沿当前源码位置执行。
- `CompileDefinition` 当前读取 `.shader` 的模块与 import 依赖，再调用 `asset.CompileAsync` 编译已经声明的入口；`ClusterShadersBlock` 当前只声明 Operations。M2 需要补齐几何组合与目标特化表、入口源码生成及其导入接入。
- `ClusterShadeMode<debugMode>` 已把模式常量传给 `CSShade`；运行时阶段选择优先读取该已烘焙变体，普通 Deferred 模式仍保留回退至 `ClusterShade` 的路径。统一入口时以特化模式 0 覆盖该回退。

**改完后**
- 引擎提供泛型入口模块：
  - `cluster_raster_entries.slang`：`ClusterRasterEntries<M : IMaterial, TOutput : IClusterRasterOutput, TInput : IRasterInput>`。
  - `shade_surface_entries.slang`：`ShadeSurface<M, TInput : IShadeInput, TOutput>`。`TInput` 有两种：`VisibilityBufferInput`（从着色 bin 取像素，读可见性缓冲和深度缓冲）和 `LayerFragmentInput`（从着色 bin 取层片元记录，读记录中的片元身份和深度）。入口沿用三角形与重心坐标重建，不透明输入额外写运动向量，再调用同一材质的 `evaluateSurface<TOutput>`。材质通过泛型目标写出最终结果。调试模式使用已烘焙的编译期特化。
  - `cluster_tessellation_entries.slang`：只为声明了细分曲面的材质生成。
  - 层合成没有按材质生成的入口：层可见性光栅是 `ClusterRasterEntries<M, LayerVisibilityOutput, TileClusterInput>`，层着色是上面的 `ShadeSurface<M, LayerFragmentInput, LayerPayloadOutput>`，排序与合成是不依赖材质的引擎 shader（T6）。
- 材质的 `.shader` 定义只列出材质模块。导入器（`SlangShaderImporter`）根据 `IMaterial` 的几何组合和管线声明的目标特化表，生成入口模块 `<材质名>.entries.slang` 并编译。特化列表写进 `ShaderSourceBlock.Specializations`，加载时全部就绪（上一份方案 B2）。
- 管线的特化表由 `ClusterShadersBlock` 声明，包括：屏幕可见性、阴影页、层可见性三种光栅输出；批次输入、溢出输入、tile cluster 输入三种光栅输入；可见性缓冲、层片元两种着色输入；屏幕颜色目标 `OpaqueOutput`、片元载荷目标 `LayerPayloadOutput` 两种着色目标。用户 pass 以自己的写出目标特化同一材质程序。
- 生成的实例布局 `render_instance_layout.slang` 由引擎入口模块 import，材质通过其自身声明解释顶点数据。
- 光栅面由材质类型的 `RasterFaces` 烘焙声明选择；相同完整着色实现的 `front` 与 `both` 用途使用各自的入口组合并复用同一材质库。公开 `Material.State`、`MaterialState` 和 `SurfaceMode` 退役（ARCH-26）；用户 Visibility 决定覆盖与深度，`Material.Layer` 决定层路由。aggregate 包围继续使用已有 `Material.BoundsExpansion` virtual 属性及实例半径链，Domain 包围服务原始三角形和 patch。

**判定**
- ARCH-24 通过。
- 文件系统检查：`samples/DefaultGame/assets/Shaders/` 中的独立透明材质入口合并至泛型目标入口。
- BEH-M2：非 PBR 材质（法线可视化或卡通分阶光照）只写一份 `evaluateSurface<TOutput>`，导入后在不透明和层合成两种用法下都能渲染，并且能接收阴影。

### M3 VSM 页请求不再经过材质

**现状**
- 不透明的页请求由一个单独的材质 pass（`ClusterMaterialEntries.ShadowRequests`）完整执行一遍材质表面求值后调用 `MarkVirtualShadowPages`。
- 透明的页请求由 `RequestTransparencyPages` 再跑一遍投影、覆盖和表面重建，而且一个线程按三角形包围盒逐像素循环（[cluster_transparency.slang](../assets/Shaders/cluster_transparency.slang)）。
- 当前 `MarkVirtualShadowPages` 只接收位置、视图和 first-person 位；`CullBackfacingPixels` 的声明位于请求参数中，几何法线背面测试由采样库使用。`ReconstructVirtualShadowReceiverPixel` 的邻点保持相机深度，用于 footprint；几何法线重建需要使用 `VirtualShadowReceiverDepthStep` 修正邻点深度。
- first-person 位当前由材质通过实例数据取得。局部阴影视图映射当前依赖 point / spot 的连续 GPU 缓冲偏移；L2 的统一优先级顺序需要共用的显式 GPU 灯光到阴影视图映射。

**改完后**
- VSM feature 持有请求库模块 `vsm_page_requests.slang`（`MarkVirtualShadowPages` 及其资源声明）。有两个调用方：
  - 不透明：新 VSM pass `vsm_visibility_requests.slang`，输入为深度缓冲。位置由深度重建，背面剔除用的法线由深度的屏幕空间导数得到。不透明请求必须在光栅完成后从深度生成，因为光栅时片元还不知道自己会不会被覆盖。
  - 层：`LayerVisibilityOutput`（T4）在片元通过 `Visibility.evaluate` 和不透明深度测试、写出片元记录的同时调用请求库。层片元全部参与合成，所以就地标记是精确的。tile 工作组先在 groupshared 中对（视图, 页）去重，再写全局请求位图。不做背面剔除。
- 两类请求都在 VSM 分配之前完成，全部层的请求当帧生效。
- 不透明几何法线使用屏幕深度邻域重建切向量：先由 `VirtualShadowReceiverDepthStep` 取得邻点深度差，再用对应 clip 坐标和 `ClipToWorld` 重建世界坐标差。
- first-person 接收类别沿已有可见性身份 → 可见 cluster → 实例编号 → 引擎实例数据分类交付；层光栅在光栅时已持有可见 cluster，直接取得相同类别。这些接收标志由可见性 resolve 和层光栅持有，数据沿现有 Rendering world 通路传递。
- VSM 按 L2 的统一 GPU 灯光顺序提供每盏灯的首个阴影视图下标，请求和采样共用该映射。point face 与 spot 视图选择使用相应 `ShadowViews` 的灯光种类和面信息。
- 删除 `ClusterMaterialEntries.ShadowRequests`、`ClusterPipelineSystem.ShadowRequests.cs`、`ConfigureShadowRequests`、`RequestTransparencyPages`。

**判定**
- ARCH-25 通过。
- 源码检查：`ClusterMaterialEntries` 中不存在 `ShadowRequests` 常量。
- BEH-M3a：一个以泛型目标着色、使用阴影采样库的材质，在方向光下的阴影图像与标准材质相同几何的阴影图像在指定区域内一致。
- BEH-M3b：64 层透明平面叠在同一屏幕区域，每层落在方向光 clipmap 的不同页上。第一帧的阴影图像与"全部页常驻"的参考一致，VSM 质量指标中层着色回退到粗页的次数为 0。
- PERF-M3：三场景帧时间与 A2 基线对比，去掉的材质请求 pass 时间单独列出，层光栅中请求标记的耗时随 PERF-T6 的层可见性光栅一起报告。

## 7. 分层合成（T 组）

结构概要：
- tile 是层合成的基本单位。cluster 按包围盒分到 tile（T3），tile 作为光栅目标，与不透明的屏幕、VSM 的页并列（T2）。
- 层光栅只写可见性：每个通过测试的片元在所在 tile 的片元存储里追加一条记录（T4）。
- 层着色与不透明着色使用同一套着色分箱和同一个材质程序，结果写进该记录对应的载荷（T5）。
- 片上只做两件事：按像素对全部片元排序，再按顺序合成（T6）。
- 没有候选槽位、轮次和层数上限。唯一的限制是片元存储容量，容量不够时报告并扩容。

### T1 `LayerComposition` 模块

**现状**
- 透明、X-ray、有序合成、贴花都写在 `ClusterPipelineSystem` 里（[ClusterPipelineSystem.Transparency.cs](../src/SomeEngine.Render.Cluster.Pipeline/ClusterPipelineSystem.Transparency.cs) 等）。非 Cluster 几何无法加入同一套排序。
- 工作区中尚未提交的 `src/SomeEngine.Render.Features/Layers/`、`assets/Shaders/layer_tile_lists.slang` 采用 K 槽加轮次的结构（`CandidatesPerRound`、`MaximumRounds`、`LayerSortTileLists`、`Grow()`），按本节改写。
- 共享着色分类和合成通过 `fragment_material.slang` 从原始片元身份解析材质地址，几何描述符与页读取范围来自同一 `RenderGeometryResources`。片元与载荷的存储按下列清单落实。

**改完后**
- 目录 `src/SomeEngine.Render.Features/Layers/`，公开类型：
  - `LayerComposition`：一个 Device 一个。拥有：
    - tile 网格，`TileSize` 为 16 或 32；
    - 片元存储：块池 `LayerFragmentBlocks`（每块 `BlockSize` 条记录，共 `BlockCapacity` 块）、与记录一一对应的载荷缓冲 `LayerFragmentPayloads`、每个 tile 的块链头 `LayerTileHeads`；
    - 累加器、`LayerStencil`。
  - `LayerPassDesc`：`Name`、`SortMode { DepthFrontToBack, DrawOrderThenDepth, DepthBackToFront }`、`ClipByOpaqueDepth`（bool）、`Accumulator { Color, ColorDepthStencil }`。载荷类型为 `LayerColor`（T5）。
  - `LayerPassId LayerComposition.AddPass(in LayerPassDesc desc)`：内部开启并提交一次图编辑，提交成功后才保存 pass、参数缓冲 ID 和注册次序。多个 pass 按注册顺序依次作用于同一个累加器。
  - `void LayerComposition.RemovePass(LayerPassId pass)`：内部编辑并提交移除，再更新已提交的注册列表。
  - `ReadOnlySpan<LayerPassDesc> LayerComposition.Passes`：按合成顺序返回已提交的 pass 描述。调用方先提交自己资源的编辑，再把已提交的 ID 交给合成器。在调用方的活动编辑中调用会触发 RG 的单编辑限制。
  - `MaterialLayer`（值组件，挂在材质上）：所属 pass、`DrawOrder`（`byte`）、合成操作（见 T6）、`StencilReference`。替代原来 `MaterialState` 里的合成字段。
  - `LayerCompositionMetrics`（公开诊断，来自读回）：`FrameIdentity`（读回 tag）、`Fragments`（本帧写入的片元记录数）、`MaxFragmentsPerPixel`、`BlocksUsed`、`FragmentOverflow`（因块池用尽而没有写入的片元数）、`SplitTiles`（分段合成的 tile 数）、`SlowPathPixels`（走单像素归并路径的像素数）。
- 容量：
  - `OnReadback` 只保存测量结果和来源帧 tag，不改图；
  - 合成器自己的 `RenderPrepareSystem` 在 `TryOpenNextFrame` 交付读回之后、`BeginFrame(next)` 之前运行。`FragmentOverflow > 0` 或 `BlocksUsed` 超过容量的 3/4 时，它自持一次编辑，用 `SetBufferDescription` 扩大块池和载荷缓冲，提交成功后才采用新容量。扩容方法不公开。
- 删除 ARCH-27 列出的全部文件，退役 ARCH-26 列出的公开状态类型与成员。

**判定**
- ARCH-26、ARCH-27、ARCH-35、ARCH-37、ARCH-38 通过。
- ARCH-18（上一份方案）通过。
- BEH-T1a：调用方持有一个活动编辑时调用 `AddPass`，抛出 `InvalidOperationException`，`Passes` 与 `RenderGraph.StructureVersion` 都保持原值。
- BEH-T1b：`AddPass` 返回后，`Passes` 包含新 pass，`StructureVersion` 恰好增加 1，下一帧的合成结果包含该 pass。
- BEH-T1c：块池设为需求的一半，在途帧数 F 为 2。
  - 来源帧 N 的读回报告 `FragmentOverflow > 0`，`Metrics.FrameIdentity` 等于 N 的帧身份；
  - 读回送达的那一帧（按槽位复用为 N+F）在 `BeginFrame(next)` 之前，prepare 提交一次扩容，`StructureVersion` 恰好增加 1；
  - 这一帧的图像与参考一致，读回的 `FragmentOverflow == 0`；
  - N+1 至 N+F−1 帧沿用当时已提交的容量，验收只观察帧 N 和扩容生效后的帧。

### T2 层 tile 作为光栅目标

**现状**
- Cluster 已有光栅目标机制：剔除时为每个 cluster 计算目标窗口（`VisibleClusterRasterWindow`），光栅输出通过 `IClusterRasterOutput` 抽象。但屏幕与目标的区别是运行时的 `targetPolicy == 0u` 分支，散布在 [cluster_policy_raster_output.slang](../assets/Shaders/cluster_policy_raster_output.slang) 和 [cluster_raster_shared.slang](../assets/Shaders/cluster_raster_shared.slang) 中。

**改完后**
- `ViewTargetKind` 增加 `LayerTiles`。`LayerComposition` 在准备阶段为每个主视图创建一个层视图实体（`RenderViewTarget { Kind = LayerTiles }`），页尺寸等于 tile 尺寸。
- 透明 cluster（材质的 `MaterialLayer` 存在）在 Cluster 剔除阶段：
  - 只进入层视图，不进入屏幕可见性；
  - 用 cluster 包围盒（细分 cluster 用 `MaterialBounds`）算 tile 窗口；
  - `ClipByOpaqueDepth` 为真的 pass 对不透明 HiZ 做遮挡测试。
- **不展开任何三角形或细分图元。**
- 光栅输出改为编译期类型参数，三种实现：`ScreenVisibilityOutput`、`ShadowPageOutput`、`LayerVisibilityOutput`。删除 `ClusterViewUniforms.TargetPolicy` 字段及全部运行时分支。

**判定**
- ARCH-23 通过。
- 元数据检查：`ClusterViewUniforms` 没有 `TargetPolicy` 字段。
- BEH-T2：一个细分透明材质（`deformed-phong`）在 `Opacity = 1` 时的层合成图像，与同一个 mesh 作为不透明材质渲染的图像在指定区域内一致。

### T3 tile 的 cluster 列表

**改完后**
- `LayerComposition` 是 `ScreenBinning` 的使用方。条目是（层视图中的可见 cluster, tile 窗口），cell 是层 tile。
- tile 列表**不排序**：合成顺序由 T6 对片元的排序决定，cluster 的先后不影响结果。删除 `LayerSortTileLists` 及其双缓冲。
- 按光栅阶段（材质的 Vertex、Domain、Visibility 组合）分组，生成（tile, 光栅阶段）工作项，以及每个光栅阶段的间接派发参数。

**判定**
- ARCH-28、ARCH-29、ARCH-38 通过。
- 源码检查：图中不存在按列表容量循环录制的 pass。

### T4 层可见性光栅

**改完后**
- 每个光栅阶段一次间接派发 `ClusterRasterEntries<M, LayerVisibilityOutput, TileClusterInput>`，一个工作组负责一个（tile, 光栅阶段）工作项：
  1. 光栅本 tile 列表中属于该阶段的 cluster。细分在这里现场遍历，屏幕范围参数传 tile 矩形。材质的 `Visibility.evaluate` 在这一步执行，被丢弃的片元不写记录。`ClipByOpaqueDepth` 为真的 pass 用不透明深度做逐片元测试。
  2. 每个通过测试的片元写一条 12 字节的记录 `LayerFragment`：
     - `Depth`：完整的 32 位深度；
     - `Identity`：片元身份（可见 cluster 下标, 三角形）；
     - `Packed`：tile 内像素下标（10 位）、pass 序号（6 位）、`DrawOrder`（8 位）、front-face（1 位，第 24 位）、保留（7 位）。front-face 供 T6 的正面/背面 stencil 操作使用。
  3. 软件光栅（SW）持续追加：每个 wave 持有自己的当前块和记录数，由首个活跃 lane 为本 wave 取得连续记录区间，并广播给各 lane 写入。wave 的状态分别保存在 groupshared 中，以普通读写推进。首次写入申请当前块，随后持续填入该 wave 的记录；当前块写满后再取新块。每次取块对块池计数器执行一次 `InterlockedAdd`，并用一次 `InterlockedExchange` 把新块挂到 `LayerTileHeads[tile]` 的链头。块头保存 tile、下一块和记录数。wave 结束时写入末块的实际记录数，并按本 wave 的写入和丢弃数量汇总指标。
  4. 同一步按 M3 标记 VSM 页：tile 内先在 groupshared 中去重，再写全局请求位图。
  5. 块池用尽时，后续片元不写入，计入 `FragmentOverflow`（T1 的扩容规则）。
- 块内记录的顺序取决于执行顺序，并不确定。T6 按（排序键, 片元身份）排序，所以最终图像是确定的。
- 细分光栅写入原始三角形身份和实际光栅深度，着色阶段据此调用用户 `Domain.reconstruct`。
- 大窗口 cluster（覆盖的 tile 数超过 `LayerComposition.HardwareWindowTiles`）走硬件光栅路径，像素着色器写同样的记录。硬件光栅（HW）把当前 wave 中属于同一 tile 的有效片元组成独立批次，每批为 `count` 个片元申请 `ceil(count / BlockSize)` 个新块，并写入各块的实际记录数。全局原子分为：每个（wave, tile）批次一次块池 `InterlockedAdd`，每个新块一次链头 `InterlockedExchange`，成功写入时一次 `Fragments` 指标累加，发生丢弃时一次 `FragmentOverflow` 指标累加。
- HW 各批次独立使用新块，块利用率按批次计。默认 `BlockSize = 128` 时，单 tile 的 32 个有效片元批次申请一块，记录区利用率为 25%；64 个有效片元批次同样申请一块，利用率为 50%。wave 分散到多个 tile 或仅有部分 lane 产生片元时，各批次的记录区利用率随之降低。
- 删除 K 槽、64 位原子插入、"已处理位置"、提前停止、`LayerRetireClusters`。

**判定**
- BEH-T4a：两张相交的异色半透明平面（alpha 0.5），图像与 CPU 逐片元排序后合成的参考图在 1/255 以内一致；倒序提交实体后，图像逐字节相同。
- BEH-T4b：alpha test 树叶卡片场景，`LayerCompositionMetrics.Fragments` 等于 CPU 参考算出的"未被丢弃的片元数"。
- BEH-T4d：把 `HardwareWindowTiles` 分别设为 1 和最大值，让同一场景全部走硬件路径和全部走软件路径，两张图像在指定区域内一致，`Fragments` 相同。

### T5 层着色

**改完后**
- 复用不透明的着色分类：`ClassifyShadeTile` 按输入类型特化，输入可以是可见性缓冲，也可以是层 tile 的片元存储（沿块链读取记录，从片元身份取得材质槽位）。分类结果是每个材质的工作项。
- 每个材质一次间接派发 `ShadeSurface<M, LayerFragmentInput, LayerPayloadOutput>`。它与不透明的 `ShadeSurface<M, VisibilityBufferInput, OpaqueOutput>` 调用同一份 `evaluateSurface<TOutput>`，结果写到该记录在 `LayerFragmentPayloads` 中的同一下标。
- 着色输入从记录中取片元身份和 `Depth`，恢复原始三角形；`ResolveBarycentricsAtDepth` 用深度反投影目标位置，并将原始三角形、目标位置和屏幕导数输入交给用户 `Domain.reconstruct`。不透明与层着色共用这条反算流程。
- 载荷 `LayerColor`：已预乘颜色（half3）和彩色透射率（half3），共 12 字节。深度、`DrawOrder`、pass 序号来自记录；合成操作和 stencil reference 来自 `MaterialLayer`。材质只决定颜色和透射率。
- 删除运行时 uber-shader 的生成：`GetTransparencyShaders`、`ComposeTransparencyShaders`、`CreateTransparencySource`、`FindTransparencyType`。

**判定**
- ARCH-10（上一份方案）、ARCH-25、ARCH-27、ARCH-39 通过。
- PERF-T5：在 `glass` 效果上把透明材质种类从 1 增加到 8，层着色的 GPU 时间按材质种类线性增长，不出现单 kernel 寄存器溢出（报告中给出每个材质着色入口的寄存器数）。

### T6 片上排序与合成

**改完后**
- 按 pass 的注册顺序，每个 pass 一次派发 `LayerComposite`，一个工作组负责一个有记录的 tile：
  1. 沿块链读出本 pass 的记录，在 groupshared 中按 tile 内像素下标分桶（计数排序）。
  2. 对每个像素的全部片元排序：
     - `DepthFrontToBack`：按（深度由近到远, 片元身份）；
     - `DepthBackToFront`：按（深度由远到近, 片元身份）；
     - `DrawOrderThenDepth`：按（`DrawOrder` 升序, 深度由近到远, 片元身份）。

     每个像素的片元数在 16 以内时，由一个线程在寄存器中做插入排序；更多时，在 groupshared 中做 bitonic 排序。
  3. 按排好的顺序读取载荷，执行合成操作。某个像素的透射率恰好为 0，或当前操作声明为不透明时，这个像素后面的片元不再读取载荷。结果仍然精确。
  4. 累加器只写一次。
- 容量分三级，三级的结果都是精确的：
  - tile 的全部记录能放进 groupshared：一次处理完；
  - 放不下：先统计每个像素的片元数，把 tile 按像素分成若干段，每段的记录能放进 groupshared，各段依次处理，计入 `SplitTiles`；
  - 单个像素的片元数超过 groupshared 容量：这个像素在显存暂存区做分段归并后合成，计入 `SlowPathPixels`。
- 合成操作表 `LayerOperation` 是数据，不是代码分支：
  - 颜色操作：`Over`（标量 alpha）、`Transmit`（彩色透射）、`Add`、`Multiply`、`Min`、`Max`；
  - 深度操作：测试和写入，对象可以是累加器深度或不透明深度；
  - stencil 操作：比较函数、读写掩码、通过/失败/深度失败时的处理；
  - X-ray：根据片元是否被不透明遮挡，选择两组颜色参数。
- 内核是固定的 switch，与材质无关。原来的 `ClusterStencil` 迁到 `LayerComposition` 内，改名 `LayerStencil`，保留 `Export`/`Import` 接口。
- 删除 `MaterialState` 中的近似误差，删除所有"一个材质设置影响全部像素"的全局开关：原来的 `MaximumError` 取最小值、X-ray 强制精确、`Effects ≠ 0` 时关闭深度截断。

**判定**
- BEH-T6a：同一像素叠 64 层，图像与 CPU 逐片元排序的参考一致，`SplitTiles == 0`、`SlowPathPixels == 0`。
- BEH-T6b：同一个 cluster 内两个三角形互相穿插，同一像素上两者的片元交替出现。图像与 CPU 逐片元排序后合成的参考在 1/255 以内一致。
- BEH-T6c：一个像素叠的层数超过 groupshared 容量（例如 4096 层）。图像与参考一致，`SlowPathPixels > 0`。
- BEH-T6d：一个覆盖整个屏幕的透明三角形叠 8 层，图像与参考一致；tile 尺寸为 32 时 `SplitTiles > 0`，图像同样一致。
- BEH-T6e：现有 `ordered` 效果（stencil 窗口）与 `xray` 效果的图像，与改动前在指定区域内一致。
- BEH-T6f：`DrawOrderThenDepth` 下，`DrawOrder = 0` 的平面位于 `DrawOrder = 1` 的平面之后（离相机更远），合成时仍然先合成 `DrawOrder = 0` 的平面。图像与按此顺序算出的 CPU 参考一致。
- PERF-T6：用 `tools/Measure-ClusterEffects.ps1`（通过 `Invoke-Benchmark.ps1` 提交）测量 8 类效果在三场景上的帧时间，与改动前的 XYZ 结果放在同一份报告中，配置一致。任何一项帧时间回退都不能视为通过。报告拆分以下各项：
  - 层可见性光栅（含页请求标记）、着色分类、层着色、排序与合成四段 GPU 时间；
  - 记录与载荷的字节数、块池峰值；
  - `SplitTiles`、`SlowPathPixels`。

### T7 帧内顺序

**改完后**，帧内执行顺序固定为：
1. 不透明光栅；
2. 层 cluster 分箱（T3）；
3. 层可见性光栅，同时标记层的 VSM 页请求（T4）；
4. 不透明深度的 VSM 页请求（M3）；
5. VSM 分配与光栅；
6. 不透明着色；
7. 层着色（T5）；
8. 层排序与合成，按 pass 的注册顺序（T6）；
9. TAA。

`LayerComposition` 通过 `ClusterGraphRegions` 中的具名 region 与 Cluster、VSM 排序，不再直接引用 Cluster 管线的私有 region。

**判定**
- 源码检查：`LayerComposition` 中的 `OrderAfter` 只引用 `ClusterGraphRegions` 公开的 region 和 VSM feature 公开的 region。

## 8. VSM（V 组）

### V1 按灯光识别缓存

**现状**
- 页表区段按视图下标顺序紧凑分配（[VirtualShadowMap.cs:574-583](../src/SomeEngine.Render/Shadows/VirtualShadowMap.cs)）。
- 视图数量一变（`viewLayoutChanged`），所有灯的全部缓存页都作废（[VirtualShadowMap.cs:592](../src/SomeEngine.Render/Shadows/VirtualShadowMap.cs)）。
- 页元数据记录 `ViewIndex`。

**改完后**
- 新类型 `VirtualShadowLightKey(VirtualShadowLightKind Kind, Entity Light, uint Face, int ClipmapLevel)`。
  - 方向光的 `Face` 表示现有视图 family：regular 为 0，first-person 为 1，`ClipmapLevel` 使用绝对级别；点光的 `Face` 使用立方体面编号，spot 为 0。实体身份来自 `RenderLightSet`，GPU 灯光下标只用于本帧查找。
- 新类型 `VirtualShadowPageTableRanges`：`Dictionary<VirtualShadowLightKey, PageTableRange>` 加空闲区段链表。
  - 灯光存在期间，它的区段固定不变；
  - 灯光移除时，区段的 `RangeGeneration` 递增后放回空闲链表。
- `VirtualShadowView` 增加 `RangeId` 和 `RangeGeneration`。`VirtualShadowPageMetadata` 用 `RangeId` 和 `RangeGeneration` 替换 `ViewIndex`。
  - view 复用原有两个 32 位 padding，保持 448 字节；页元数据保留投影内容 `Generation`，大小为 36 字节。
  - `ShadowRanges` 按 `RangeId` 索引，行格式为 `uint4(PageTableOffset, 有效页表项数, RangeGeneration, 本帧ViewIndex)`。区段整段复用，offset 随 range ID 单调；页表地址通过同一表二分查找当帧视图。有效项数按该 view 实际布局填写，复用大区段时排除剩余容量。
  - `VirtualShadowPages` 持有唯一的区段分配器，`VirtualShadowFrame` 保存当帧键和区段提议；所需资源容量在准备阶段的图编辑提交后采用，区段与投影历史在 `OnFrameSubmitted` 中采用。
- 页池的 `PublishOwnedPage` 在 `RangeGeneration` 不匹配时回收该页。
- 删除 `viewLayoutChanged` 以及依赖它的全量重置。视图下标只用于本帧派发，不参与缓存身份。

**判定**
- ARCH-31 通过。
- `VirtualShadowViewQuality` 新增公开字段 `DirtyPages`。
  - `DirtyPages` 从同帧 `ActivePhysicalPages` 的页表地址按 view 区段统计；`Projection` 提供质量读回中的实际 `VirtualShadowView`，供行为检查计算页覆盖范围。
- BEH-V1：静态场景、一盏方向光，收敛后加入一盏远处的投影点光。下一帧所有方向光视图的 `DirtyPages == 0`。

### V2 删除全局作废

**现状**
- 实例数量一变，所有视图换新的缓存代数（`structuralContentChanged`）。
- `PrepareViews` 的 `geometryRevision` 和 `rasterRevision` 参数，调用方固定传 0（[VirtualShadowFeature.cs:887](../src/SomeEngine.Render.Features/Shadows/VirtualShadowFeature.cs)）。

**改完后**
- 删除 `structuralContentChanged`、`_committedInstanceCount`、`_committedGeometryRevision`、`_committedRasterRevision`，以及 `PrepareViews` 的三个参数。
- 实例增加时按新包围盒失效，实例删除时按旧包围盒失效，都进入现有的投影物失效流程（V5 改为负载均衡版本）。

**判定**
- ARCH-31 通过。
- BEH-V2：静态场景收敛后，在一盏方向光下新增一个小物体。下一帧的 `DirtyPages` 大于 0，并且不超过"该物体包围盒加页膨胀范围覆盖的已分配页数"（测试用公开的视图参数在 CPU 上计算这个上界）。

### V3 时间历史按视图判断

**现状**
- `_forceStateReset` 关闭预过滤历史、负载历史和上一帧页 HiZ。触发条件包括：实例批次换了对象（`ReferenceEquals`）、场景中有任一无界材质且本帧有实例变化（[VirtualShadowFeature.cs:839](../src/SomeEngine.Render.Features/Shadows/VirtualShadowFeature.cs)、[:880](../src/SomeEngine.Render.Features/Shadows/VirtualShadowFeature.cs)）。

**改完后**
- 删除 `_forceStateReset`、`_previousForceStateReset`。
- 每个视图的 `HistoryValid` 由三个条件决定：上一帧存在同一个 `VirtualShadowLightKey`、投影相同、资源没有重建。写入 `VirtualShadowView.Flags` 的 `PreviousProjectionValid`，并在 `VirtualShadowViewQuality` 中公开。历史查找按 key 对应旧视图，GPU 灯光下标与当帧视图下标只承担派发和映射职责。
- 无界材质的实例通过既有四行 `InstanceWorldBounds` 的 w 分量保存当前/历史无界事实，xyz 保留普通几何消费者使用的范围。投影物失效流程把该事实解释为对应视图已分配 mip 的完整矩形，只作废该实例影响的页。
- 实例批次是否变化，改为比较 `ContentRevision`。

**判定**
- ARCH-31 通过。
- BEH-V3：1024 小球场景（每帧移动全部实例），加入一个无界细分材质的静态物体。在不受小球影响的灯光视图上，`HistoryValid` 连续为真。

### V4 预过滤远景不再禁用缓存

**现状**
- 启用 `PrefilteredDistantEnabled` 后，方向光的深度中心改为每帧的相机深度（[VirtualShadowMap.cs:394-396](../src/SomeEngine.Render/Shadows/VirtualShadowMap.cs)），所有方向光视图每帧换新代数（[VirtualShadowMap.cs:638](../src/SomeEngine.Render/Shadows/VirtualShadowMap.cs)）。

**改完后**
- 预过滤模式与普通模式使用同一个经过吸附的深度中心 `SelectDirectionalDepthCenter`，深度范围覆盖 `cullingEndDistance`。
- 删除每帧换新代数的循环。预过滤历史只针对 dirty 页更新。

**判定**
- BEH-V4：开启预过滤，静止相机静态场景收敛后，方向光视图的 `DirtyPages == 0`，阴影图像与关闭预过滤时在近处区域内一致。

### V5 投影物失效负载均衡

**现状**
- 本轮已将原来的逐实例串行页遍历接入 [vsm_caster_binning.slang](../assets/Shaders/vsm_caster_binning.slang) 的 Count／Scan／Expand 路径；性能判定沿下列要求执行。

**改完后**
- 投影物失效成为 `ScreenBinning` 的使用方。条目是（变化的实例, 视图, mip, 当前／历史包围盒），矩形是该 mip 上的页矩形与已分配矩形的交集。`ScreenBinningExpand` 每页一个线程，执行现有的单页失效逻辑。
- 只用到 B1 的前三步，不需要 cell 列表。
- 固定的当帧 region 容纳按实际条目数创建的 Count、扫描与 Expand 子图；页池先于 Count，页分配依赖 Expand 完成。
- 几何包围盒前两行保存当帧范围，后两行保存上一提交范围。VSM 根据已提交实体身份取得旧行，消费移动、删除与行复用产生的旧范围；几何反馈区在消费完成后更新历史两行。无界事实随两份范围分别保存，覆盖对应 mip 的已分配矩形。

**判定**
- ARCH-28、ARCH-29 通过。
- PERF-V5：移动一个包围盒覆盖整个方向光 clipmap 的投影物，失效 pass 的 GPU 时间与改动前对比，在同一份报告中给出。

### V6 滤波请求传播

**现状**
- 每帧对整个页表派发线程，没开预过滤也执行（[VirtualShadowFeature.Prefilter.cs:245-247](../src/SomeEngine.Render.Features/Shadows/VirtualShadowFeature.Prefilter.cs)）。
- 读取请求标志时，其他线程同时在写别的页的标志，处理的集合不确定。

**改完后**
- `PropagateVirtualShadowFilterRequests` 改为遍历 `RequestedPageIndices`，派发数取自传播开始前写好的间接参数。处理的集合固定为本 pass 开始时的请求。
- `SetPassEnabled(_prefilterRequestPass, Settings.PrefilteredDistantEnabled && views.Length != 0)`。

**判定**
- 源码检查：该 shader 不含以 `PageTableEntryCount` 为界的线程判断。
- BEH-V6：开启预过滤，同一帧重复渲染两次（固定输入），请求的页集合相同。

### V7 receiver mask 层级不跨区域

**现状**
- 层级归约用 `VirtualShadowPagesPerRow(view)`（mip 0 的页数）作为上限，粗 mip 的 texel 会把相邻 mip 区域合并在一起（[vsm_allocate_pages.slang](../assets/Shaders/vsm_allocate_pages.slang) 的 `BuildReceiverMaskHierarchy`）。

**改完后**
- `BuildReceiverMaskHierarchy` 和 `ResetVirtualShadowReceiverMasks` 的归约上限改为 `VirtualShadowMipPagesPerRow(view, mipLevel)`。
- 光栅过滤 `ClusterTargetReceiverHierarchyAccepts` 中，读取的 mip 上限为 `log2(该区域页数)`。
- 各区域原点已经按自身尺寸对齐（mip L 区域的 x 偏移是 `P>>L` 的倍数，视图原点是 `P` 的倍数），只限制归约层数就能避免跨区域合并。

**判定**
- 源码检查：上述三处不再使用 `VirtualShadowPagesPerRow(view)` 作为归约上限。
- PERF-V7：方向光场景的阴影光栅批次数（`VirtualShadowViewQuality` 中的可见项计数）与改动前对比。

### V8 清理死代码与每帧分配

**改完后**
- 删除 `VSM_PAGE_LOCKED`、`VSM_PAGE_RECEIVER_DEPTH`，以及所有与 `VSM_PAGE_LOCKED` 比较的分支。
- 视图参数写入 VSM 持有的持久设备缓冲：登记时调用 `AddBufferUploadPass`，逐帧调用 `WriteBuffer` 写入视图有效前缀。`GraphBufferInput` 将同一持久 ID 的有效切片交给现有消费者与质量读回；空视图沿用一项 default 的范围。缓冲随视图容量在准备阶段的显式编辑中扩容，提交成功后采用容量。
- 基础阴影偏置使用 Feature 持有、随视图容量增长的工作数组。`QualityRequest` 复制该帧实际请求的视图前缀，保留到异步读回齐备。`SelectDistantUpdates` 的 eligible、更新集合和当帧投影映射使用跨帧复用容器，选择器复用排序列表与活动集合；已提交年龄只在 `OnFrameSubmitted` 推进。公开 `Select` 继续返回调用方独立持有的数组，主渲染路径直接写入当帧集合。

**判定**
- ARCH-32、ARCH-33 通过。

### V9 页池压缩改用共享扫描

**现状**
- `CompactAvailableShadowPages` 由单个 256 线程工作组循环处理全部物理页（[vsm_page_pool.slang](../assets/Shaders/vsm_page_pool.slang)）。

**改完后**
- 改用 `ScreenBinningScan` 的多工作组前缀和。保持现有 LRU 顺序语义：可用列表按上一帧请求顺序排列，空页在末尾。

**判定**
- 源码检查：`vsm_page_pool.slang` 不再含 `PagePrefix[256]` 的 groupshared 扫描。
- BEH-V9：页池压力测试（请求页数超过物理页数）下，被淘汰的页是上一帧最早请求的页，可通过公开的 `VirtualShadowMipQuality.MappedPages` 变化和图像回退结果观察。

### V10 动态层刷新改为设置项（决策 D12）

**改完后**
- 新增 `VirtualShadowMapSettings.DirectionalDynamicReceiverRefresh`。
  - 为真时：保持现有行为，方向光的动态层每帧重画，动态投影物不做失效。
  - 为假时：动态投影物也走 V5 的失效流程，动态层可以缓存。
- `vsm_allocate_pages.slang` 中的 `receiverRefresh` 和 `vsm_caster_binning.slang` 的覆盖范围选择都读取这个设置。

**判定**
- PERF-V10：1024 小球场景在两种设置下的 VSM 光栅时间和帧时间，结论写进 Virtual-Shadow-Maps 页，并据此确定默认值。

## 9. wiki 更新

| 工作项 | wiki 页 |
|---|---|
| B1 | 新增 rendering/Screen-Binning |
| L1~L4 | features/Lighting |
| DE1 | rendering/Material-Decals |
| M1~M3 | material/Material、material/Stage-Compatibility、shader/User-Stage-Interfaces、features/Virtual-Shadow-Maps |
| T1~T7 | 新增 rendering/Layer-Composition；rendering/Cluster-Blend-Integration 标注已被取代；features/Translucency 改为指向 Layer-Composition |
| V1~V10 | features/Virtual-Shadow-Maps |
| ARCH-23~39 | harness/Architecture-Checks |

## 10. 验收汇总

| 工作项 | ARCH | BEH | PERF |
|---|---|---|---|
| B1 | 28、29 | B1a、B1b、B1c | |
| B2 | 34 | B2 | |
| L1 | 28、29 | L1 | |
| L2 | | L2 | |
| L3 | | L3 | |
| L4 | 30 | | L4 |
| DE1 | 27、28、35 | DE1，现有贴花效果图像 | |
| M1 | 24、25、36 | 现有全部图像测试 | |
| M2 | 24 | M2 | |
| M3 | 25 | M3a、M3b | M3 |
| T1 | 18、26、27、35、37、38 | T1a、T1b、T1c | |
| T2 | 23 | T2 | |
| T3 | 28、29、38 | | |
| T4 | | T4a、T4b、T4d | |
| T5 | 10、25、27、39 | | T5 |
| T6 | | T6a、T6b、T6c、T6d、T6e、T6f | T6 |
| T7 | | | |
| V1 | 31 | V1 | |
| V2 | 31 | V2 | |
| V3 | 31 | V3 | |
| V4 | | V4 | |
| V5 | 28、29 | | V5 |
| V6 | | V6 | |
| V7 | | | V7 |
| V8 | 32、33 | | |
| V9 | | V9 | |
| V10 | | | V10 |

所有 PERF 判定通过 `tools/Invoke-Benchmark.ps1` 提交。渲染相关的测量使用 `tools/Measure-Scenes.ps1` 的三场景平台，透明效果额外使用 `tools/Measure-ClusterEffects.ps1`。分辨率、场景行为、渲染选项、JIT 配置和采样方式与上一份方案 A2 的基线保持一致；带 trace 的定位结果与性能结果分开记录。未收敛、失败或缺场景的报告不能作为通过依据。
