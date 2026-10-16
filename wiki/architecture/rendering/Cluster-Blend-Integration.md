# Cluster 多覆盖混合

规格 [#221](https://github.com/ODtian/SomeEngine.Next/issues/221) 将普通网格的透明材质、完整材质 X-ray、材质贴花和有序颜色/stencil 纳入 Cluster 管线。研究与性能基线见 [tile visibility 研究](Transparency-Tile-Visibility.md)。本页记录正式功能的数据通路、使用入口和验收对应。

## 数据与执行

游戏实体持有网格、材质和效果组件；提取系统更新 Rendering world；Cluster 管线从该世界形成 GPU 实例数据、源图元与候选引用。`Material.State` 与材质属性共用资源修订机制。顶点解码、变形、细分曲面与表面材质由用户 shader 求值。

几何 cluster 表示网格分组。Forward+ cell 表示屏幕 XY tile 与视空间 Z 分区。多覆盖功能复用光照视图的分区约定，先计数、保留连续范围，再散布图元引用。XYZ 命中按自身深度所属 cell 执行一次；XY 将每个 tile 的全部深度合到一个 cell，逐命中求值一次。容量设置决定驻留批次，后续源图元继续参与覆盖与合成。

每个透明命中重建自己的位置、UV、法线、导数与 `SurfaceReceiver`，执行自己的材质与阴影求值。OIT 依真实深度推进颜色与透射率。`Material.State.MaximumError = 0` 遍历全部贡献；正值结合材质声明的 `ColorLimit` 限定尾部误差。

`Material.State.Ordered` 将材质放入 OIT 之后的有序阶段。`DrawOrder` 指定作者次序，相同次序按实例、几何及图元身份推进。`BlendAttachmentState` 表达颜色与 alpha 的因子、运算和通道写入；`BlendConstant` 提供常量混合值。`DepthStencilState` 与 `StencilRef` 表达深度、正反面的 stencil 比较、读写掩码及 stencil-fail、depth-fail、pass 操作。覆盖先经过用户材质的可见性求值，再执行完整状态链；颜色写掩码为零的命中仍推进 stencil。

有序材质通过共用实例属性保留主视图 HiZ 后的覆盖，深度比较在状态链内选择 depth-fail 操作。像素线程保留当前颜色、深度与八位 stencil，按作者次序完成全部覆盖。最终 stencil 写入一张 R32UInt 整数图。

持久 RenderGraph 保存投影、计数、范围保留、引用散布和求色阶段。每帧上传及 GPU 引用内容沿这些阶段更新；几何、实例、视图、驻留或相关渲染设置变化时刷新索引。静态输入复用既有数据。

材质的边界能力按实例使用的完整材质序列汇总，光栅阶段持有 `MaterialBounds` 及其 GPU 数据。根播种、层级遍历、LOD、HiZ 和 cluster 筛选读取各实例自己的能力；空间组保存组内能力的汇总。用户 shader 的域可以声明 `CLUSTER_TESSELLATION_BOUNDS "finite"`，调用方通过 `SceneEdit.AddEntity(mesh, materials, transform, boundsExpansion)` 提供世界单位外扩。

Wave/Phong 示例采用六个二次控制点的凸包确定外扩：`.35` 缩放部件使用 `.66`，公共单位球使用 `1.31`。推导和实例剔除的实际工作量见 [Cluster 工作列表](../cluster/Worklists.md)。透明投影默认驻留 `524288` 个微三角形；容量控制缓存与按源单元重投影的选择，两条路径保留相同命中和材质求值。

## 统一场景

DefaultGame 使用 `--cluster-effect=<效果>` 选择专项内容，`--blend-index=full|xy|xyz` 选择完整遍历、XY tile 或 XYZ 候选，默认使用 XYZ。三种方式消费相同的几何、用户材质和效果规则。`--cluster-static` 固定效果物体，静态性能对照同时固定场景行为。

`LightingFeature.CreateTileGrid()` 提供 XY 候选的同视图参数，沿同一份视图数据派生 `DepthSliceCount=1` 与每视图 tile 数。透明与材质贴花绑定这份参数后，现有 cell 构建、遍历和最终命中所属 cell 查询自然收敛到单切片。PBR 光照使用原来的 16 切片参数。两种索引共用 counts、offsets、引用与容量规则，各 cell 缓冲按所选 grid 的实际 cell 数分配。

2026-10-16，`XyAndXyzCandidatesMatchFullScanWhileModelsAndCameraMoveAcrossCells` 在集成提交 `a4451752` 与配套 review 内容上通过 D3D12、Vulkan 两项公共图像检查。每个后端分别运行 Full、XY、XYZ 与投影/引用容量均为 1 的 XYZ，各 16 帧；第 7、15 帧采集物体和相机移动前后的整图，线性颜色容差为 0.001。全部内容页驻留，保留 `WaitForFrames=false`；默认行为规则检查为 0 违规，测试进程退出码为 0。

| 效果 | 场景内容 |
| --- | --- |
| `glass` | 32 个交错玻璃面板，四组材质与八层相交覆盖。 |
| `smoke` | 八团烟雾，每团 16 张带 alpha 纹理的卡片。 |
| `foliage` | 64 组植被，每组三张交叉叶片。 |
| `deformed-phong` | 16 个移动部件，使用 Wave 顶点变形与 Phong 曲面。 |
| `xray` | 64 个机械部件，共 10,752 个三角形，带独立遮挡板与完整 Standard 材质。 |
| `decal-layers` | 16 组表面，每组包含三层玻璃、opaque 面板与 16 层材质贴花，共 256 个体积。 |
| `decal-sparse` | 64 个局部贴花体积覆盖墙面，透明前景与墙面分别使用命中位置求值。 |
| `ordered` | 四个旋转 stencil 窗口，共 16 个面板，依次执行遮罩写入、alpha 混合、乘色和通道混合。 |

场景内容通过 `SceneTestPlatform.InitializeScene` 和 `UpdateScene` 编辑游戏实体。相机回放时专项物体使用同一相机相对布局，三个基础场景都保有对应的效果工作量。

## 自定义硬件 stencil 阶段

`ClusterPipelineSystem.GetStencil()` 按消费者请求创建共享整数图。应用的 `IApplicationColorFeature` 通过 `ApplicationRendering.Stencil` 取得同一能力；`SceneScript.CreateColorFeature` 提供脚本应用的相同入口。整数图与硬件阶段的区段长期保留，材质变化更新其生产依赖。

调用 `stencil.Export(ref edit)` 创建 D32FloatS8UInt 附件，声明整数图打包与 stencil-plane copy。硬件 pass 使用返回的 `Texture`，并排在 `CompletePass` 之后；该 pass 持有自己的深度输入、颜色附件与 RHI stencil 状态。随后调用 `attachment.Import(ref edit, hardwareComplete)`，通过反向 copy 与解包将 stencil 返回同一整数图。后续阶段继续读取 `stencil.Values`，也可以再次导出为硬件附件。

```csharp
ClusterStencil stencil = rendering.Stencil!;
ClusterStencilAttachment attachment = stencil.Export(ref edit);
// 在 stencil.Region 添加硬件 pass，附件取 attachment.Texture。
edit.OrderAfter(hardwarePass, attachment.CompletePass);
GraphPassId completed = attachment.Import(ref edit, hardwarePass);
```

有序像素状态处理与显式交接进入 `MaterialCompositeMs`。R32UInt 图、材质状态、交接缓冲和硬件附件进入显存口径；交接缓冲按 RHI 的 stencil plane footprint 组织行距和各视图偏移。

```powershell
& 'F:/SomeEngine.Next/tools/Invoke-Benchmark.ps1' pwsh -NoProfile -File ./tools/Measure-ClusterEffects.ps1 -ApplicationPath ./templates/DefaultProject/Game.App/bin/Release/net10.0/publish/Game.App.exe
```

本机共享入口 `F:/SomeEngine.Next/tools/Invoke-Benchmark.ps1` 管理排队与执行。性能相关的启动、测量和捕获通过该入口提交；队列任务完成后读取结果报告。

效果脚本调用 `tools/Measure-Scenes.ps1`，每次同时测量 1024 小球、Sponza、Amazon Bistro，以完整且收敛的三场景报告交付对照。默认对八类效果逐一配对 XY 与 XYZ：XY 保留屏幕 tile 内的候选，XYZ 继续按深度切片筛选。每个场景采集固定的 64 个模拟帧，Wave/Phong 从第 192 帧开始；各效果的窗口由脚本统一指定。`-Methods full,xy,xyz` 可加入逐像素完整遍历作为图像和工作量参考。配对固定分辨率、模拟帧、场景行为、渲染配置、编译配置与采样。内容构建单列；trace 定位单列。

测量结果用于当轮读取：`Measure-ClusterEffects.ps1` 默认写入 `artifacts/cluster-blend-scenes.json`，研究场景脚本写入 `artifacts/transparency-scenes.json`，研究应用的两组对照分别写入 `artifacts/transparency-comparison.json` 与 `artifacts/transparency-effects.json`。调用方读取结果后立即删除对应 JSON，图像与日志沿同一使用周期清理。

## X-ray

网格实体的 `Xray` 组件分别持有可见侧与遮挡侧的颜色倍率、透明度，以及遮挡距离区间。编辑入口与其他游戏数据一致：

```csharp
edit.SetXray(entity, new Xray(
    new XrayBlend(.8f),
    new XrayBlend(new Vector3(.7f, .9f, 1), .45f),
    MinimumOcclusion: 0,
    MaximumOcclusion: 8));
```

`RemoveXray` 恢复该实体的普通材质路径。独立提取器更新镜像上的 `RenderXray`，其 48 字节设置随共用实例属性提交。primary hierarchy 与 cluster 的遮挡判断保留效果几何，继续执行视锥和 LOD 选择；阴影视图沿原材质路径工作。求色以该命中自己的表面和 receiver 完成，随后按 opaque 深度分类并应用对应侧别规则。距离使用视空间的世界单位。

别名堆的访问交接由 RenderGraph 的 `Placement.Aliases` 派生。D3D12 的别名屏障把缓冲交到 `NO_ACCESS`，紧随其后的首次声明使用显式 `ALL/NO_ACCESS` 到目标同步范围与访问类型的屏障完成激活。这与 [D3D12 Enhanced Barriers 的别名规则](https://microsoft.github.io/DirectX-Specs/d3d/D3D12EnhancedBarriers.html#resource-aliasing) 一致，适用于单点光照网格、引用与覆盖范围共享临时堆的情形。普通缓冲的隐式首次访问继续沿原规则处理。

`XrayRenderingTests` 的三项公共图像对照已通过：D3D12 和 Vulkan 各运行 56 帧，覆盖可见侧与遮挡侧的独立混合、效果添加与移除、实体克隆与移动后的完整图像；D3D12 另运行 32 帧，将遮挡侧的受阴影材质与同一材质的硬件图像比较，并通过移动遮挡物确认阴影更新。三项保留 `WaitForFrames=false`、常规堆复用和 0.003 线性颜色容差，运行入口同时执行行为规则检查。

## 计量口径

`--measure-blending` 产生完成 GPU 提交后的逐帧 `ClusterBlendMetrics`，通过模拟帧关联统一平台的呈现间隔。测量采用图形队列的校准时间戳；统一性能工具同时关闭异步计算以固定阶段顺序。

| 字段 | 工作范围 |
| --- | --- |
| `ProjectionIndexMs` | 当前几何的用户投影、候选计数、连续范围与引用构建，逐 pass 计时求和。复用帧为零。 |
| `CoverageReceiverMs` | 覆盖与各命中阴影 receiver 请求。VirtualShadowFeature 按当帧阴影视图数启停请求区段，零视图帧为零。 |
| `MaterialCompositeMs` | 命中筛选、表面与材质求色、颜色和状态合成，以及硬件 stencil 区段中的 pack、export、hardware draw、import、unpack。每个实际 pass 分别计时并求和。 |
| `DecalIndexMs` | 贴花 Clear、Count、Reserve、Scatter、Sort 五个 pass 的 GPU 时间之和。复用帧为零；空集合刷新计入当帧实际执行的清空与构建成本。 |
| `OpaqueMaterialMs` | 每个 opaque receiver 与 color 用户 pass 分别计时后求和，覆盖这些 pass 内耦合的基础材质、贴花属性组合及光照求值。 |
| `Triangles` / `References` | GPU 记录的实际工作量。 |
| `DecalVolumes` / `DecalReferences` / `DecalResidentReferences` | GPU 工作缓冲回读的实际体积、投影引用总量与池内驻留引用数。 |
| `UsedBytes` / `CapacityBytes` / `AllocationBytes` | 当帧驻留数据、配置容量和 RHI 分配要求，包括贴花私有缓冲、stencil 图像、256B 私有 stencil uniform upload 及硬件交换资源。 |
| `DecalUsedBytes` / `DecalCapacityBytes` / `DecalAllocationBytes` | 总量中的贴花分项，包含 144B 体积、用户参数字节、Counts/Offsets/Writes、引用、每视图体积范围与 Work；统一 uniform arena 按贴花参数 slice 归属使用/容量字节。 |
| `ReusedIndex` | 当前帧复用已有投影及候选内容。 |
| `ReusedDecalIndex` | 当前帧复用已有贴花候选内容。 |

`SceneTiming.Blending` 汇总五项时间的平均值、中位数、P95、两类索引的构建/复用帧数、体积/引用及显存；整帧呈现间隔保留在原 `SceneTiming` 字段。逐帧原始数据保存在统一报告的 `BlendFrames`。共享纹理、网格驻留和统一实例存储沿各自的数据持有者另计。

计量由 Cluster 管线持有，纯 opaque 贴花同样产生数据。每个测量 pass 使用一对时间戳，附着在该 pass 的命令前后；CPU 按阶段累加各对差值。回读大小为 `16 × 测量 pass 数量 + 36` 字节，末尾分别保存透明的 5×uint 与贴花的 4×uint GPU 工作数。透明项包含实际微三角形、引用总数、驻留引用、active cluster 及 source bounds 数量；使用字节计入每个 active cluster 的 12B 请求与 4B 索引、预算内每份 source bounds 的反射 stride，以及 20B 工作数和 60B 间接调度参数。容量和分配按两份实际缓冲描述分别累加。缓存与重投影的两套 pass 时间归入同一阶段。测量对象统一持有查询池、结果缓冲与回读；用户分组、资源或硬件阶段显式变更时调整测量集合。本帧禁用的 pass 清零结果范围，查询解析只读取本帧启用的 pass。每个提交保留当帧已更新的缓冲容量和 RHI 分配描述，回读以同一提交的数据计算。

`BlendMeasurementsFollowDecalChangesAcrossSubmittedFrames` 已通过 D3D12 的 16 帧公共启动检查：引用预算为 1，纯 opaque 场景在第 8 帧从一个贴花增至两个，GPU 数量、驻留引用、使用字节及容量按提交更新，随后恢复候选复用。`OrderedRenderingTests` 的双次硬件 stencil 往返已在 Vulkan 开启计量通过，全图线性颜色容差为 0.001。

2026-10-16 的逐 pass 计时修复以统一平台的实际异常为起点：零贴花场景曾记录约 13.63 ms 贴花索引，与投影索引相同。时间戳移到各 pass 命令两侧后，D3D12 的静态候选检查在第 7、15 帧确认投影和贴花复用时间均为零；关闭虚拟阴影时 receiver 请求为零，透明颜色工作保持正值。纯 opaque 贴花的 16 帧检查确认更新帧索引与用户求值为正、复用帧索引为零。Vulkan 双次硬件往返的 16 帧检查取得全部逐帧计量，并在 tile 和硬件参考帧确认合成时间为正，整图容差为 0.001。以上为行为与计量边界证据，三场景性能结果沿统一工具报告读取。

`SceneTiming.Instances` 在场景完成时记录共用实例存储的实际行数、批次数、容量与属性字节数，效果增加的实例属性进入该项；共享几何仍由 `GeometryUsedBytes` 等既有字段记录。

XY 额外的候选视图参数由同一 LightingFeature 持有，透明与贴花共用。计量在统一使用与容量字节中计入一次 `192 × viewCount`；上传 arena 的 RHI 分配沿既有共享分配口径记录。PBR 光照继续使用原来的 16 切片参数。

## 行为与交付对应

所有图像检查经过 `Application.RunAsync`、`SceneScript`、游戏编辑和线性图像读回，失败图沿公共设施输出实际、期望与差异 PNG。默认 `dotnet test` 同时执行 [行为检查规则](../../harness/Behavior-Checks.md)。

| 规格故事 | 任务 | 公共行为检查与实现 |
| --- | --- | --- |
| 1–5：网格材质、用户求值、每命中属性、相交排序、光照阴影 | [#222](https://github.com/ODtian/SomeEngine.Next/issues/222) | `TransparencyRenderingTests` 的完整排序与全不透明材质对照；`Material.State`、源图元和用户表面求值。 |
| 6、7、15：动态曲面、光栅入口与容量分段 | [#223](https://github.com/ODtian/SomeEngine.Next/issues/223) | `TransparencyGeometryTests` 从用户 Wave 顶点变形和 Phong 曲面生成透明几何，对比硬件、软件、单项三角形与引用容量，并对比脚本移动和启动时的相同变换。 |
| 7、15：硬软语义、容量分段 | [#224](https://github.com/ODtian/SomeEngine.Next/issues/224) | `TransparencyIndexTests.XyAndXyzCandidatesMatchFullScanWhileModelsAndCameraMoveAcrossCells` 在两种后端对比完整遍历、XY、XYZ 常规容量和 XYZ 单项容量。 |
| 8、9、14：完整材质 X-ray、两侧规则与连续编辑 | [#225](https://github.com/ODtian/SomeEngine.Next/issues/225) | `XrayRenderingTests` 对比同一 Standard 材质的硬件可见表面与 X-ray 命中，检查纹理、两侧透明度、独立遮挡体阴影，以及移入、移除、替换实体和返回后的画面。 |
| 10、11、14：光照前材质贴花与逐命中空间查询 | [#226](https://github.com/ODtian/SomeEngine.Next/issues/226) | `MaterialDecalRenderingTests` 对照指定次序的分层颜色，移动贴花体积经过前后两层透明面后移除；`LitDecalNormalAndRoughnessMatchTheSameStandardSurface` 将受光 coating 的法线与粗糙度同普通 Standard 材质比较。 |
| 14：游戏编辑与当前帧更新 | [#224](https://github.com/ODtian/SomeEngine.Next/issues/224) | `StaticBlendCellsAreReusedUntilInstanceMembershipChanges` 与 `SceneInitializationAndFrameEditsMatchTheSameScriptedScene`。 |
| 16：尾部误差界 | [#222](https://github.com/ODtian/SomeEngine.Next/issues/222) | 完整排序图像跨越四项局部队列继续求值；同一检查把材质误差界改为 0.04，与精确图比较整图误差。 |
| 12、13：有序颜色与 stencil | [#227](https://github.com/ODtian/SomeEngine.Next/issues/227) | `OrderedRenderingTests` 分别对照只读遮罩、alpha 与乘色次序、stencil 的 fail/depth-fail/正反面与读写掩码。参考通过公共颜色 feature 使用 RHI 固定功能状态，并将同一整数图两次导出、导入硬件附件。 |
| 17、18：专项与整帧、统一场景 | [#228](https://github.com/ODtian/SomeEngine.Next/issues/228) | `ClusterBlendMetrics`、DefaultGame 效果入口与 `Measure-ClusterEffects.ps1`。 |

`OrderedRenderingTests` 的三类 D3D12 硬件对照均已通过，整图线性颜色容差为 0.001。Vulkan 的 stencil 写入检查以同一容差通过，并开启 `--measure-blending`，覆盖整数图与硬件附件的两次往返、正反面状态、depth-fail 和读写掩码。每项经过 16 个脚本帧，运行入口同时执行行为规则检查。

实际 pass 计时通过同一 Vulkan 硬件检查：公共 `BlendFrameCompleted` 交付 16 份逐帧统计，tile 图像对应的第 7 帧与硬件图像对应的第 15 帧均有大于零的 `MaterialCompositeMs`。计时附着于实际执行的 pass，覆盖 stencil 的打包、附件传输、硬件使用与解包链。

交付分支为 `codex/tile-blending-221`；[PR #229](https://github.com/ODtian/SomeEngine.Next/pull/229) 汇集全部规格工作。
