# Virtual Shadow Maps

本页维护现行渲染架构与 Shader API。实现入口为 [VirtualShadowFeature](../../../src/SomeEngine.Render.Features/Shadows/VirtualShadowFeature.cs)、[页标记函数](../../../assets/Shaders/vsm_receiver_requests.slang)、[覆盖位读取](../../../assets/Shaders/vsm_receiver_mask_bits.slang)与[材质阴影采样](../../../assets/Shaders/vsm_forward_sampling.slang)。

VSM feature 持有页表、atlas/cache、请求、页分配与阴影策略。它构造普通 render views，并通过几何管线执行阴影视图；Cluster 按统一视图完成几何与光栅阶段。

## 页标记

调用方在自己的管线中计算需要阴影的位置，并在合适的处理阶段调用：

```slang
void MarkVirtualShadowPages(float3 worldPosition, uint viewIndex, bool firstPerson = false);
```

`worldPosition` 使用当前渲染坐标系；`viewIndex` 指明对应的相机视图。调用方负责从几何、深度或自己的计算取得该位置，并完成第一人称位置变换。VSM 函数立即标记页请求和页内覆盖位，再将首次请求的页编号加入工作列表。

Cluster 在既有页请求阶段通过用户 Vertex 与 Domain 取得已分箱表面的位置，完成第一人称变换后直接调用该函数。透明请求沿实际三角形投影和像素覆盖取得当次命中深度，通过相机逆投影还原渲染位置并直接标页；投影和覆盖复用本次请求内的局部数据。VSM 持有标页行为，材质接口持有表面求色行为。调用链见 [Cluster 页请求](../../../src/SomeEngine.Render.Cluster.Pipeline/ClusterPipelineSystem.ShadowRequests.cs)、[位置求值](../../../assets/Shaders/cluster_shade_pipeline.slang)与[透明请求](../../../assets/Shaders/cluster_transparency.slang)。

声明 `CLUSTER_FORWARD_DEPTH_POSITION` 并保存 `ClusterPrimaryDepthShadowRequests` 特化的材质，
在普通 Forward 请求中由管线读取已分箱像素的主深度并直接标页。
Standard 的 Forward 着色使用同一个深度位置，并保留完整曲面数据用于法线、纹理与运动矢量。
含第一人称网格时，材质请求通过原表面求值取得位置；页请求继续进入本页的同一个标记函数与覆盖资源。
材质接口与选择规则见 [User Stage Interfaces](../shader/User-Stage-Interfaces.md)。

`VirtualShadowMapShadersBlock.MarkDepthPages` 显式保存共享主深度请求 Shader 的引用，
`VirtualShadowMap` 持有对应管线。`VirtualShadowFeature` 持久安装
`MarkVirtualShadowDepthPages`，在全部材质的已保存 Forward 入口声明 `PrimaryDepthReceiver` 时启用。
该 Shader 读取当前主深度与 Rendering world 视图，对每个可见像素调用同一个 `MarkVirtualShadowPages`；
页请求与逐像素覆盖沿本页的同一资源和分配顺序更新。
使用共享 pass 的帧关闭每材质请求 region；第一人称或自定义接收解释通过材质请求处理。
实现见 [共享请求](../../../src/SomeEngine.Render.Features/Shadows/VirtualShadowFeature.DepthRequests.cs)与
[主深度 Shader](../../../assets/Shaders/vsm_mark_depth_pages.slang)。

页标记根据提供的位置、视图投影参数和动态精度偏移选择方向光 clipmap 级别或局部光 mip。请求范围覆盖过滤半径与法线偏移，并处理相邻页、覆盖单元和点光源相邻面。

## 每页基础请求数据

| 内容 | 每虚拟页大小 | 表达内容 |
| --- | --- | --- |
| 页请求标志 | 4 B | 页的请求状态 |
| receiver mask | 8 B | `uint2` 表达页内 `8×8` 覆盖 |
| 合计 | 12 B | 基础请求数据 |

mask 使用两个垂直排列的 `R32UInt` texel，各保存 `8×4` 位覆盖，供现有 32 位图像原子操作写入。覆盖读取、层次归约、光栅裁剪、预过滤与质量读取共用该表示。

帧内顺序为：请求计数复位、视图更新及调用方标记、预过滤请求传播、页池与分配、阴影光栅和预过滤、已消费 mask 与页请求标志清理。

[页池整理](../../../assets/Shaders/vsm_page_pool.slang)将存活缓存页按原有相对顺序写入 AVAILABLE，再按原顺序追加 EMPTY 页。
[页分配](../../../assets/Shaders/vsm_allocate_pages.slang)从 AVAILABLE 末端取页，先复用空页，再回收最久未请求的缓存页。
整理入口用一个 256 线程组分块扫描；8 个共享位字分别表示连续 32 个原位置，通过位计数计算每页的写入偏移。

## 采样与缓存

`VirtualShadowMap.PrepareViews` 根据当前光源与相机填写视图记录。投影矩阵与该位置原有记录相同时，
新记录复用其 UV 投影与法线转换矩阵；投影变化时通过 `VirtualShadowView.LightViewProjection`
执行原有派生运算。每次填写的业务字段从默认值开始，已提交视图与页缓存历史仍由帧完成路径推进。

远处预过滤的逆投影与历史视图索引直接写入图声明的映射上传缓冲。
该缓冲按光源视图容量保存，沿 `ReconcileLights` 扩容；每帧按当前视图数填写，
Shader 使用同一份 `ViewCount` 读取有效记录。实现见
[预过滤数据](../../../src/SomeEngine.Render.Features/Shadows/VirtualShadowFeature.Prefilter.cs)与
[光源容量](../../../src/SomeEngine.Render.Features/Shadows/VirtualShadowFeature.Capacity.cs)。

材质使用当次求得的位置、法线和深度偏移直接执行 VSM 采样。方向光和局部光沿 soft shadow、第一人称与屏幕射线起点处理；局部 `SurfaceReceiver` 保存本次函数采样参数。

驻留读取在本级命中时复用已经读取的页表项、页坐标和视图；粗层回退继续读取实际来源层。
页项解码共用完成状态与部分深度的解释，PCSS、SMRT 和硬阴影继续使用相同的采样位置与深度运算。

Standard Forward 在单像素的灯光循环前计算接收位置对应的投影参数和局部可见度量化噪声。
局部 PCSS 在一次采样中共用实际来源视图的光源方向、正交基和立体角面积；各 tap 继续使用各自的采样点与分离距离。

默认材质采样使用 PCSS。`DirectionalRayCount` 或 `LocalRayCount` 为正数时，对应光源使用已有的 SMRT 射线追踪，并读取每条射线采样数、自适应射线数量、斜率外推及抖动设置。两条采样路径共用当前页表、第一人称处理与屏幕射线起点；方向光继续合并远处预过滤，局部光继续量化和抖动最终可见度。应用参数 `--vsm-ray-count 0` 同时将过滤半径设为零，选择硬阴影比较。

页原点沿光视图的 X/Y UV 方向滚动。相机跨过对齐边界时，固定世界几何的细页地址保持连续。clipmap 每次滚动四分之一页表；较粗 mip 按投影变化重新绘制。目标页的驻留状态决定当前可采样的精度，并沿既有页回退路径取得结果。

参见 [[architecture/rendering/Multi-View]] · [[architecture/cluster/Cluster-Scope]]。
