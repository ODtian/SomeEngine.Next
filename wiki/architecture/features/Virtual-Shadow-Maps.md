# Virtual Shadow Maps

本页维护现行渲染架构与 Shader API。实现入口为 [VirtualShadowFeature](../../../src/SomeEngine.Render.Features/Shadows/VirtualShadowFeature.cs)、[页标记函数](../../../assets/Shaders/vsm_receiver_requests.slang)、[覆盖位读取](../../../assets/Shaders/vsm_receiver_mask_bits.slang)与[材质阴影采样](../../../assets/Shaders/vsm_forward_sampling.slang)。

`VirtualShadowFeature` 编排 VSM 的准备和图资源绑定；具体数据按职责持有：

| 类型 | 数据和行为 |
| --- | --- |
| `VirtualShadowPages` | RG 持久页表、atlas、页分配和页状态资源；页表通过 `SetBufferDescription` 扩容。 |
| `VirtualShadowViews` | 阴影投影计算、已提交的视图历史，以及 RenderWorld 中的阴影视图实体。 |
| `VirtualShadowRequests` | 页请求列表、请求标志和 receiver mask。 |
| `VirtualShadowPrefilter` | 预过滤资源与具名的轴向、合并、请求、选择常量输入。 |
| `VirtualShadowQuality` | 工作量、质量读回与 `RenderFeedback<VirtualShadowQualitySample>`；记录包含观测种类和值，以 RG 帧号标记。准备阶段按 `LastCompletedFrame` 消费精度和耗时观测。 |
| `ShadowCasterCache` | 实例变化、静态年龄、实体身份和细分包围体的缓存分类。 |

准备阶段通过 `RenderGraphNextFrame.GetFrameData` 取得 `VirtualShadowFrame`，计算视图并写入 RenderWorld。执行阶段通过 `RenderGraphFrame.GetFrameData` 取得同一份数据，记录 caster 变化、工作参数与历史选择。帧提交观察者在 `OnFrameSubmitted` 接受这份状态。RG 持有资源状态和完成后的资源回收。

投射物失效由 `VirtualShadowCasterExpansionPasses` 在固定的当帧 region 中登记 Count、共享多工作组 Scan 和 Expand。条目取实际的变化记录数 × 视图数 × mip 数 × 当前／历史两份包围盒，偏移、扫描中间量和间接派发参数按当帧长度创建。每个展开线程处理一个已分配页，沿页表、Range 代数与世界页坐标确认归属，再更新该页的失效状态。页池先于 Count，Expand 完成后执行分配。

准备工作的持久声明顺序为视图更新、粗页标记、滤波请求、页池、投射物当帧 region 和页分配。视图更新与滤波请求各自在预先创建的 region 内登记，RG 据这些区间连接当帧资源访问与前后 pass。

几何为每个实例保留四行包围盒：前两行由 `BuildClusterSceneBounds` 写入当帧范围，后两行保存上一提交的范围。`ShadowCasterCache` 用已提交的实体表生成当前行到旧行的映射，并将删除实体的旧行加入变化记录。VSM 读取旧行时按 `PreviousBoundsOrigin` 转换渲染原点；几何反馈区的 `CommitClusterSceneBounds` 在当帧失效消费完成后复制前两行到后两行。包围盒的 `.w` 分别保存当帧和历史的无界事实；无界投射物直接覆盖对应视图、mip 的已分配矩形。LOD 变化继续携带原实例行身份与细分范围，使用同一展开流程。

视图参数的设备存储由 `VirtualShadowFeature` 持有的 `_shadowViewBuffer` 图编号确定，构造时按视图容量创建持久缓冲并登记一次 `AddBufferUploadPass`。`StageFrame` 通过 `WriteBuffer` 写入当帧视图前缀，再把该持久 ID 的有效切片交给既有 `_frameShadowViews` 输入。GPU 工作量调整继续写这份设备缓冲；着色、页请求和质量读回通过输入取得当帧的实际范围。空视图保留一项 default 的上传及映射范围。RG 的上传页保存待提交字节，并按帧提交、丢弃及 GPU 完成边界管理其生命周期。

灯光增长和设置变化沿准备阶段的既有图编辑扩展视图缓冲、页请求、receiver mask 及过滤历史。新描述按目标视图容量计算，`Commit` 返回后写入 `_sourceViewCapacity` 与请求标志容量，并扩展 Feature 的基础偏置工作数组。逐帧记录填写工作数组的有效前缀；质量请求复制本次视图数量对应的偏置，读回齐备时用这份独立数据解释实际 GPU 视图结果。

Feature 同样持有 CPU 渲染视图、上一帧阴影视图和上一帧投影矩阵的容量数组。`VirtualShadowFrame` 以 `Memory<T>` 借用实际视图数对应的前缀，准备阶段及记录阶段通过 `Span` 同步消费；Prefilter 的旧视图索引、上传长度和 `PreviousViewCount` 都使用这一实际数量。数组随构造和成功容量提交扩展，异步质量请求继续持有自己的数据。

远处局部灯的投影映射、eligible 与更新集合由 `VirtualShadowViews` 复用；`CreateFrame` 将这些工作容器交给当帧实例，准备开始时清空。调度器复用候选排序列表与活动集合，选择过程读取已提交年龄，`Submit` 才更新年龄及已提交投影映射。RG 的单 CPU 帧边界保证上一帧完成提交或丢弃后才复用这些工作容器。公开 `VirtualShadowDistantLightUpdates.Select` 返回独立数组，主渲染路径把结果直接写入当帧更新集合。

每个阴影视图在 RenderWorld 中拥有同一个实体上的 `RenderView`、`RenderViewTarget { Kind = ShadowPages, TargetIndex }` 与 `RenderPreviousView`。Cluster 查询这些组件，按目标类型执行统一的几何流程。资源通过 `IGraphResourceProvider` 的名字提供；Runtime 显式传入光栅前、中间、光栅后的 region。

`VirtualShadowPages.Ranges` 保存已提交的灯光投影区段。准备阶段从 `VirtualShadowLightKey` 生成当帧提议，资源容量编辑提交后写入 view 的区段地址与代数；帧提交时采用这些键和区段。GPU 的 `ShadowRanges` 以 range ID 索引 `uint4(offset, 有效项数, 复用代数, 当前视图下标)`，页表地址反查及页 owner 验证共用该表。view 的两个原有 padding 保存 range ID 与复用代数，页元数据以这两项标识 owner，并保留投影内容代数。

投影历史按同一个灯光 key 查找，视图增删和 GPU 灯光重排沿用各自的历史。方向光 key 的 family 值 regular=0、first-person=1，与绝对 clipmap 级别共同区分投影；点光使用立方体面编号。`PreviousProjectionValid` 表示同 key 的投影与资源历史有效，RenderWorld 仅为这些视图保存 `RenderPreviousView`。质量结果提供对应的 `HistoryValid`、实际 `Projection`，并从 active page 记录按区段统计 `DirtyPages`。

`SetSettings` 在准备边界更新设置、RG 资源描述和 HiZ 小组拓扑，并清理相应历史。页容量变化沿同次编辑替换可用页压缩的扫描资源、派发和列表访问范围，保留页列表的图编号，并连接页池、压缩和分配的先后关系。编辑提交后采用新的压缩资源、HiZ 图编号、常量映射和设置。应用可从 `CreateFrameFeatures` 的 `ApplicationRendering.Shadows` 取得同一实例，在 `ApplicationScene.UpdateFrame` 的准备边界调用 `SetSettings`；[容量修改行为检查](../../../tests/SomeEngine.Runtime.Tests/VirtualShadowCapacityRenderingTests.cs) 对照从较大页池启动的图像与质量结果。可选质量流程按帧启用。`ReplaceShader` 使用同一个 `RenderPipelines` 替换受影响的计算管线，并在一次图修改中更新它们的 Slang 参数布局。

`VirtualShadowViews` 在生成方向光视图时将 `DirectionalDynamicReceiverRefresh` 转换为视图标志。开启时动态 receiver 深度逐帧刷新，关闭时动态投射物走失效路径并复用动态页。默认值随 PERF-V10 的两种设置测量确定。

DefaultGame 通过 `--vsm-directional-dynamic-refresh` 和 `--no-vsm-directional-dynamic-refresh` 选择两组测量设置。命令行值经 `ApplicationOptions` 进入应用默认阴影设置；脚本运行使用 `SceneScript.ShadowSettings` 的值。统一场景结果的 `DirectionalDynamicReceiverRefresh` 字段记录本次显式命令行选择。

## 页标记

调用方在自己的管线中计算需要阴影的位置，并在合适的处理阶段调用：

```slang
void MarkVirtualShadowPages(float3 worldPosition, uint viewIndex, bool firstPerson = false);
```

`worldPosition` 使用当前渲染坐标系；`viewIndex` 指明对应的相机视图。调用方负责从几何、深度或自己的计算取得该位置，并完成第一人称位置变换。VSM 函数立即标记页请求和页内覆盖位，再将首次请求的页编号加入工作列表。

不透明接收者由 `VirtualShadowOpaqueRequests` 注册独立深度请求 pass，从可见性身份、主视图深度和实例变换重建位置与几何法线。GPU 灯光下标到首个阴影视图的映射由 VSM 在本帧生成；请求先于页池与分配执行。层接收者通过 `LayerVisibilityOutput` 在可见性光栅中调用同一请求库。当帧页请求沿这两个入口进入 VSM。

页标记根据提供的位置、视图投影参数和动态精度偏移选择方向光 clipmap 级别或局部光 mip。请求范围覆盖过滤半径与法线偏移，并处理相邻页、覆盖单元和点光源相邻面。

## 每页基础请求数据

| 内容 | 每虚拟页大小 | 表达内容 |
| --- | --- | --- |
| 页请求标志 | 4 B | 页的请求状态 |
| receiver mask | 8 B | `uint2` 表达页内 `8×8` 覆盖 |
| 合计 | 12 B | 基础请求数据 |

mask 使用两个垂直排列的 `R32UInt` texel，各保存 `8×4` 位覆盖，供现有 32 位图像原子操作写入。覆盖读取、层次归约、光栅裁剪、预过滤与质量读取共用该表示。

帧内顺序为：请求计数复位、视图更新及调用方标记、预过滤请求传播、页池与分配、阴影光栅和预过滤、已消费 mask 与页请求标志清理。

## 采样与缓存

材质使用当次求得的位置、法线和深度偏移直接执行 VSM 采样。方向光和局部光沿 soft shadow、第一人称与屏幕射线起点处理；局部 `SurfaceReceiver` 保存本次函数采样参数。

默认材质采样使用 PCSS。`DirectionalRayCount` 或 `LocalRayCount` 为正数时，对应光源使用已有的 SMRT 射线追踪，并读取每条射线采样数、自适应射线数量、斜率外推及抖动设置。两条采样路径共用当前页表、第一人称处理与屏幕射线起点；方向光继续合并远处预过滤，局部光继续量化和抖动最终可见度。应用参数 `--vsm-ray-count 0` 同时将过滤半径设为零，选择硬阴影比较。

页原点沿光视图的 X/Y UV 方向滚动。相机跨过对齐边界时，固定世界几何的细页地址保持连续。clipmap 每次滚动四分之一页表；较粗 mip 按投影变化重新绘制。目标页的驻留状态决定当前可采样的精度，并沿既有页回退路径取得结果。

Shader API 的 GPU 行为检查位于 Render 测试项目：[采样与接收者重建](../../../tests/SomeEngine.Render.Tests/VirtualShadowSamplingTests.cs)、[粗页请求](../../../tests/SomeEngine.Render.Tests/VirtualShadowCoarseRequestTests.cs)、[页分配与旧映射释放](../../../tests/SomeEngine.Render.Tests/VirtualShadowPageAllocationTests.cs)、[重排活跃页的 HiZ](../../../tests/SomeEngine.Render.Tests/VirtualShadowHiZTests.cs)、[帧工作复位](../../../tests/SomeEngine.Render.Tests/VirtualShadowFrameTests.cs)。输入使用公开的 `VirtualShadowView`、`VirtualShadowRange` 和 `VirtualShadowPageMetadata`；结果通过 GPU 读回观察采样可见度、页坐标、映射身份和深度金字塔。

参见 [[architecture/rendering/Multi-View]] · [[architecture/cluster/Cluster-Scope]]。
