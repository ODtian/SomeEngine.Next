# Virtual Shadow Maps

原始输出已移入仓库外验收归档；文中的 `artifacts/acceptance/` 路径对应归档内目录。归档身份与结果摘要见 [#70 验收记录](../../../docs/agents/acceptance-70.md)。

VSM 是独立 rendering feature，拥有自己的 page table、atlas/cache、marking、allocation 与 shadow policy。

默认物理页预算为 2048，按 atlas 行宽向上取整；默认 8192×4096 深度数组容纳 2048 个 128² 页。
`MaxPhysicalPages`、纹理高度、页池和 metadata 容量来自同一配置。方向光分辨率偏置声明默认 0。
像素请求采用 Native 2×2 stride，从每个屏幕单元的左上像素读取接收输入。
过滤请求按最大 texel 支持域扩张 page/receiver mask，并跨 point cube face 接合邻面。
预滤 X 中间纹理使用 R8_UNORM，其舍入结果继续输入 Y 与历史计算。
该批规则以固定 Native 396c9f05 的 PageAccessCommon、PageManagement、Array 和 CacheManager 为依据。

VSM 构造普通 unified render views，并通过 geometry pipeline 能力使用 Cluster；Cluster 不知道这些 views 表示 shadow。VSM counters 和策略不进入 Cluster core ABI。

参见 [[architecture/rendering/Multi-View]] · [[architecture/cluster/Cluster-Scope]]。

方向光按接收位置与相机 clipmap 原点的距离选择绝对级别。`WorldUnitsPerCentimeter`
由 VSM 设置产生并写入每个公开 `VirtualShadowView`；默认 `0.01` 对应米场景。
shader 将距离转换为原版厘米，计算 `log2(distance)`，先找到未偏置级别对应的 VSM，
再读取该 VSM 的 `ResolutionLodBias`。该偏置包含配置值与相机 viewport / projection 标度。
Game/Render 三种 light records 的 `ShadowSettings` 使用共同 `LightShadowSettings`，
包含本光分辨率偏置与静止/可移动语义。既有 `LightRenderExtractor` 按值提取，
`RenderLightSet` 持有当帧光数据；方向、point、spot 投影叠加各自的偏置，scene codec 保存这些输入。
`RenderLightSetSystem` 维护渲染帧时钟和每光运动活动；默认可移动光在新增或变换更新后从因子 1
经过 10 帧降到 0，静止光因子为 0。其全局配置由系统的 `lightActiveFrameCount` 输入提供。
VSM 设置分别持有非移动与移动精度偏置，按
`nonmoving + factor * (max(nonmoving, moving) - nonmoving)` 插值，再加本光偏置。
默认移动偏置为方向光 0、局部光 1；方向光再叠加相机标度并取原版绝对偏置下界 0。
透视相机标度取投影矩阵 X 标度的绝对值，正交相机标度取 1。
正交相机宽度转换为厘米后，与 viewport 宽度和 `DirectionalMinCameraViewportWidth` 取最大值。
相机偏置为 `log2(.5 * VirtualResolution / (width * projectionScale))`。
正交 first level 使用半宽厘米的 `floor(log2(halfWidth))` 与配置 first 的最大值，绝对下界为 0。
实际级别范围和 view 数共同写入投影、请求与采样参数；4 米正交相机对应 first 7，默认 last 22。
相机可选 `CameraDepthOfField` 保存 focus 距离、infinity/min/max CoC 半径、depth blur 系数和阴影偏置强度。
这些数据通过 SceneCamera 存档与 RuntimeViewFrame 进入 RenderView，并由请求/材质使用同一当帧值。
原版 CoC 公式组合失焦半径、带符号 depth blur 和半径范围；可用 scene depth 的阴影偏置为
`min(strength, strength * abs(CoC) / 8)`。方向光先应用此接收者偏置，再确定读取本光偏置的 VSM。
默认 strength 为 0；用户 shader 也可向相同条件函数提供 CoC 像素值。
例如距离 192 厘米、first level 6 时，未偏置 index 为 1；它的偏置为 1 时选择 index 2。
页请求与材质采样共用 `SelectDirectionalVirtualShadowView`，从目标级别寻找覆盖该位置的投影。

局部光接收者遮罩关闭时，point / spot 的采样从 mip 0 开始。遮罩开启时，mip
由原版相机 nominal pixel radius 选择。共享 `VirtualShadowReceiverProjection` 从 `RenderView`
产生 screen radius `min(1 / (viewportXY * projectionXY))` 与 world→camera clip-W 平面。
请求与标准材质从原始接收位置计算世界半径，材质显式传入 Point/Spot 采样接口；
光投影将该半径转为 `abs(radius * lightScale / lightClipW) * (2 * VirtualResolution)` footprint。
其 `floor(log2(footprint) + bias)` 决定 mip，并受可用 mip 范围限制。
`LocalMipMode` 提供 All、Even、Odd；原版在边界限制后选择偶数或奇数 mip，
同一投影字段同时供页请求与采样使用。
材质采样传入接收面上 X/Y 相邻屏幕像素的世界坐标，供几何接收面和过滤梯度计算。
页请求与标准材质共用 `ReconstructVirtualShadowReceiverPixel`，从当前深度图
重建接收面相邻像素的世界坐标。每个轴采用深度差较小的前景邻居，在断层处
保持局部接收面；视口边缘沿该接收面延伸相邻坐标。
页池压力以 AVAILABLE 列表的实际可用容量和分配失败形成全局偏移，阈值默认 .85。
本帧从持久状态复制该偏移，请求和采样共同读取；超载响应为 .5，恢复经过十帧等待后响应 .1。
工作节流由 `WorkLoadBudget` 或 `WorkTimeBudgetMilliseconds` 启用，两项默认均为零；时间预算优先。
可见 cluster/window 在每个阶段解码完成后、材质/range batch 展开前登记一次 HW 或 SW，
压缩目标保存原始 shadow view index。逐视图反馈沿 RenderWorld light entity、cube face 或绝对 clipmap level
对应上一完成帧，完成帧提交该映射与 GPU 工作历史，取消帧沿最后完成数据恢复。
原版工作成本为 `0.00012176 * HW + 0.000027 * SW`；全局强度变化为
`clamp((cost - budget) * .5, -.01, .2)`，再限制在 0–1。
各陰影视图以自身成本 / HW、SW 分别最大计数所对应成本分摊强度，并以默认 .9 历史权重平滑。
工作偏移写入本帧 `VirtualShadowView.ResolutionLodBias`；请求、材质采样和质量回读共享该数据。
默认全局偏移上限为 2，方向光/局部光工作上限均为原版 99999，页池偏移先消耗全局余量。
`WorkTimeBudgetMilliseconds` 为正时安装 GPU 时间 query 与持久回读，测量当帧 target geometry；
线性预算使用 5% headroom、16 帧历史、递归 .9 权重、1% 变化阈值和 8 帧变化间隔。
超过预算的连续两帧截断旧历史，恢复时以 .9 插值；GPU 完成数据携带实际帧和已使用 fraction。
独立光栅诊断 `RasterPageArea` 使用 HW 裁剪 page-batch 面积，SW 材质 batch 权重为 64；
`RasterBatchEquivalents` 为面积 / 64。每视图质量同时给出 `HardwareVisibleItems`、`SoftwareVisibleItems`
及实际 `WorkLodAddition`，把几何工作、材质批次数和有效偏置分别表达。
固定原版依据为 `VirtualShadowMapThrottle.usf:28–165`、`VirtualShadowMapArray.cpp:2825–2931`，
以及 `DynamicRenderScaling.cpp:23–105`、`DynamicResolution.cpp:215–413`。

clipmap 使用原版绝对级别，默认 first 6 至 last 22。级别 L 的半径为
`2^(L+1) * WorldUnitsPerCentimeter`，投影为容纳滚动对齐覆盖四个半径。
各级深度半径为该半径乘 `DirectionalDepthRangeScale`，默认倍率为原版的 `1000`。
光空间 Z 中心来自该视图的实际世界原点；投影使用以该中心为中点的两倍深度半径跨度。
`VirtualShadowMapClipmap.cpp:352–458` 记录这一逐级投影规则；公开用例
`DirectionalLevelsCoverTheirNativeDepthRange` 从源世界点检验各级实际可覆盖的深度。
因此默认 first 6 的半径是 1.28 米，完整投影宽度是 5.12 米；中心 2.56 米处到达投影边界。
公开 shader 自给数据以同一字段声明其坐标单位，厘米夹具使用 `1`，米夹具使用 `0.01`。
虚拟分辨率、可用绝对级别和分辨率偏置共同限定精度。
页池容量不足或者目标页还没有完成深度时仍可能采样较粗的页，不能把回退画面视为目标分辨率。
映射不等于采样有效：新脏页仍可光栅，但直到全部必要 caster/material/surface 工作通过
才获得完成标记。接收者掩码完成只允许实际覆盖单元的采样，不能作为整页缓存或先前 HiZ。
过滤的每个 tap 都验证覆盖；缺页不伪造 blocker，也不混入一个声称有效的平均值。
`TrySampleDirectional/Point/SpotVirtualShadowMap` 分别返回有效性和可见性；缺少完整 footprint
时先尝试较粗 mip/clipmap，再返回 false 与 lit 因子。默认局部光材质明确处理 false，
后续帧仍请求缺失工作。旧 float 签名保留为兼容因子，不提供独立有效性证明。

`MeshSource` / `MeshInstance` 的 `DepthCacheClass` 经既有提取写入 `RenderMesh`，
由实例几何 producer 写入共享实例存储中的单个 uint 列。默认 Dynamic；静态输入由 Game world 明确声明。
用户的动画 vertex、domain 或 opacity 输入可声明 Dynamic；静态输入修改沿既有 caster/material 事件失效。
当前和上帧分类随既有 world bounds 的 W 分量传给失效 shader，旧、新足迹各按实际分类更新。
静态输入失效清除静态完成代号并刷新两类深度，动态输入失效保留静态完成代号。
物理页 metadata 保存静态完成代号、请求标记、重绘状态和绝对世界页身份。
开启 receiver mask 的 Dynamic 输入沿当前接收请求刷新动态深度；显式 bounds 失效供 Static/CachedDynamic 消费。
R32_UINT 深度数组由同一个 atlas 持有：slice 0 为动态/合并深度，slice 1 为静态缓存。绘制前，静态可留用页为动态纹理提供初值；
静态失效页从零开始。SW、HW、mesh 和有限 tessellation 输出共同消费同一实例分类。
静态光栅覆盖整页，动态光栅消费本帧接收者遮罩。main/post 绘制后的静态 merge 先于对应 HiZ。
遮罩页每帧保持 dynamic uncached，接收掩码扩张从留用静态深度重新形成当前动态内容。
共享 coarse 模式 2 的 ForceCacheDynamicCoarse 标记使仅 coarse 请求页留用 dynamic，
detail 请求和 static 失效仍触发所需更新。未请求的已分配页保留其失效标记直到再次请求。
质量回读分别给出 static/dynamic 的 HW/SW 可见 cluster/window；这些计数沿原版可见项边界产生。
接收者、过滤、完成有效性共用 `vsm_receiver_projection.slang`、`vsm_sampling.slang`，
generic raster adapter 共用 `cluster_policy_raster_output.slang` 与 `cluster_raster_target_output.slang`。

页表每项为 Native uint 编码：X/Y 各 10 位、LOD offset 6 位及深度/光栅/secondary 标记。
接收遮罩的局部深度状态使用保留位 26；绝对世界页与 cache generation 由物理页 metadata 唯一持有。
Directional 地址域只含 mip 0，local 地址域含完整 mip 链，SinglePage 地址域仅含一个末级表项。
`PageTableOffset` 是各实际地址域的累加起点，灯数增长按最大地址域预分配请求与 hierarchy 存储。
完成深度后，`PropagateVirtualShadowResidentPages` 在同一页表传播已完成祖先的 XY 与 LOD offset。
Local 来源是同一投影的父 mip，directional 来源是同一光源更粗的 clipmap。
本级未完成时可读取祖先；后续本级请求重新分配自己的页，祖先的物理页继续由来源持有。
遮罩来源的每个 tap 验证当前接收单元，覆盖不足时继续沿祖先查询。
图完成边界位于传播之后，取消与部分失败沿既有帧状态恢复。
Directional 页地址采用 `quarterPages * corner` 的整数移位，UV 使用
`.25 * (cornerB - scale * cornerA)`，其中 `scale = 1 / 2^levelOffset`。
coarse 深度以 `(coarseDepth - (offsetZB - scale * offsetZA)) / scale` 返回 requested 范围。
过滤使用同一 coarse 投影的 UV、深度和接收平面梯度。
原版依据为 `VirtualShadowMapPageManagement.usf:268–401`、
`VirtualShadowMapPageAccessCommon.ush:355–371` 与 `VirtualShadowMapProjectionCommon.ush:245–305`。

`DynamicHiZ` 默认 false，R32_FLOAT HiZ 从静态深度形成一份 array 存储；启用时增加动态/合并层域。
静态与动态剔除按实例分类选择对应层域，共用同一个持久纹理。
HiZ 使用图管理持久存储：main 读取先前结果后，intermediate 更新脏页，
post 消费已保留的 deferred work，再更新 final 供下帧使用。不复制整池历史，
不 replay 或重新从根发现 deferred work。未受影响的缓存页不复制或重建。
参见 [[architecture/validation/Vsm-Depth-Coverage-135]]；已有分项公开输出/局部光 RT 检查，
完整 GPU 验收仍被 native testhost 中止及未完成的默认三场景/错误版本回归等门禁阻挡。

页原点沿实际光视图的 X/Y UV 方向滚动。相机跨过对齐边界时，固定世界几何的细页地址保持不变。
clipmap 每次滚动四分之一页表，每层仅使用 mip 0；局部光保留独立的完整 mip 链。
压紧阶段按该视图的有效 mip 范围生成光栅工作。分配阶段只解码这个范围，
深度完成与 HiZ 消费分配生成的实际脏页列表。
请求范围也覆盖材质的法线偏移和过滤半径，包括相邻页和相邻接收单元，避免采样落在未绘制区域。

遮挡者变化同时使旧包围盒与新包围盒覆盖的缓存页失效，遍历该光源投影的有效 mip。
方向光缺少细页时，采样继续到较粗 clipmap 的 mip 0；末层也通过其粗页覆盖提供深度。
粗页坐标从当前 `ClipmapWorldOrigin` 投影到该层光视图产生，与距离选择共享原点。
相机在同一 snapping cell 内移动时，四个粗页仍围绕实际相机位置；正交视图沿用
相机 target 产生的同一原点。
`DirectionalCoarsePageMode` 与 `LocalCoarsePageMode` 提供原版模式 0/1/2：
`Disabled` 关闭对应粗页请求，`Dynamic` 请求粗页，`CachedDynamic` 还设置
`ForceCacheDynamicCoarse` 供缓存失效路径读取，两者默认 `CachedDynamic`。
`SinglePage` 局部光始终请求唯一末级页，并在请求、压紧、分配、失效和采样中使用同一
有效 mip 下界；局部粗页开关与这一表示条件各自生效。

阴影比较在实际 atlas texel 中心计算接收面深度。接收位置与相邻像素的光空间投影
形成接收面深度梯度，硬阴影、遮挡者搜索与过滤采样共用此梯度。默认 `DepthBias`
为 `0.000001`，在默认方向光深度范围中对应约 1 mm；`NormalBias` 为 `0.001` 世界单位。
用户材质标量和已有 texture 内容通知同样使深度失效，即使资源绑定/光栅箱不变。
几何驻留与材质 revision 继续保护深度正确性。每个阴影视图按实际投影、深度编码及几何参数
维护自己的缓存失效；固定阴影视图在 main geometry/FOV/viewport 输入变化后留用对应深度。
外部 vertex/domain/opacity 输入通过 `InvalidateCasters` 或 `InvalidateAllCasters` 报告实际变化。
滚动继续维护世界页地址。每级方向光在其 committed view 中保存已采用的光空间深度中心和半径。
当 `abs(currentZ - cachedZ) + levelRadius <= .9 * cachedRadius` 时，投影偏移继续使用该中心；
跨过 guardband 后采用当前中心并更新对应视图。图提交与内容完成继续维护该数据的推进和恢复。
原版依据为 `VirtualShadowMapCacheManager.cpp:258–315`，公共用例
`CameraDepthMovementUsesCachedRangeUntilTheNativeGuardbandIsCrossed` 检验实际世界点的投影结果。

正交视图的 `RenderView.CameraTarget` 由既有相机/视图数据提供。启用
`OrthographicProjectCameraTarget` 时，clipmap 的 XY/Z 原点及 `ClipmapWorldOrigin` 采用该目标位置；
请求与采样的 distance level 沿该同一原点计算。Runtime 相机以位置、方向和目标距离持有实际目标，
经 `RuntimeViewFrame` 进入 RenderWorld view。透视视图与默认目标输入沿相机原点建立投影。
`OrthographicEstimateClipmapLevels` 与 `OrthographicClipmapLodBias` 表达原版 first-level 控制参数，
默认分别为 true 与零；源依据为 `VirtualShadowMapClipmap.cpp:258–317,548–563`。

独立验收目标以源 glTF 三角形、实例变换、透明度、几何法线射线起点为真值，
使用同次提交的 raw/pre-TAA 输出和实际 jitter，保留全部有效接收者及边缘。
方向光、point、spot 的参考/capture 修正由 #125 单独交付；当前源码构建不能证明最终数值质量。
`tools/Test-Shadows.ps1` 是已有测试入口，不等于全部新 Runtime 行为或完整独立参考门禁。
[[architecture/validation/Shadow-Reference-2026-10-04]] 是历史记录；当前 #135 检查范围见
[[architecture/validation/Vsm-Depth-Coverage-135]]，最终 #125/#127/#81 与 Standards/Spec 仍待验收。

阴影几何选择使用每个光视图自身的投影和虚拟像素尺寸。压紧的 mip 视图保持光源投影，
并将像素宽高缩到该 mip 的虚拟分辨率；traversal 与 cluster 选择消费这份视图数据，
用户有限细分同样消费该光源投影的像素误差。每个 clipmap 因自身投影范围获得对应的几何精度。
固定光源的遮挡者流送需求也由这些阴影视图产生，沿既有 RenderWorld 与驻留反馈通路接入。
原版依据为固定提交 `396c9f059903aed5fec78ecd3d437a40c6415368` 的
`NaniteShared.cpp:195–202` 与 `VirtualShadowMapArray.cpp:4958–4965,5008–5016`。
透视几何误差以球的最近深度和视角界乘积作为投影距离；正交视图的该距离为一。
像素倍率取光投影的 Y 标度与虚拟像素高。阴影层级及 cluster 的误差使用实例最小伸缩值，
球半径使用最大伸缩值；父子几何选择消费一致的误差标度。
VSM typed view 保存原版 `ShadowMaxPixelsPerEdge=1`、`ShadowMinPixelsPerEdgeHardware=32` 与
`ShadowDicingRate=2`。`ShadowGeometryLodBias` 默认为一，其倍率为 `2^(-bias)`；原版 caller
`ShadowSceneRenderer.cpp:142–145,1167–1174` 将该倍率与 streaming quality 相乘后交给阴影视图。
当前初始 streaming quality 为一，倍率为 `.5`，对应最大几何误差两虚拟像素。
普通 `RenderView.GeometryLodScale` 从该 typed view 取得同一倍率，compact 将该份参数交给
Cluster 的独立视图。有限用户 domain 以世界空间边长和最小视图深度生成 dicing factors，
domain 自身提供表面误差；primary 沿其已有用户配置。streaming 压力质量调整由 #147 的
实际 visible-page demand 数据接合。原版独立手算例和公开 RHI 输出见
`artifacts/acceptance/ticket-137/projection-error/`。
当前深度代号继续消费几何驻留与材质输入 revision；局部几何/材质失效在 #147 与静态/动态缓存
#141 中接合每个遮挡者的实际影响范围。

## 用户 receiver 输入

用户 shader 通过 `ISurfaceReceiverEvaluate<TSurface, TTriangle>.evaluateReceiver` 返回
32字节 `SurfaceReceiver`：shading normal、参与/模型 flags、geometric normal 和额外层的 camera depth。
可选 `ClusterSurfaceReceivers` entry 复用 primary visibility 的材质分箱与用户 vertex/domain 求值，
位于 primary visibility 完成之后、阴影请求之前。图中的单一 receiver buffer 由 VSM 请求和最终
材质消费；standard 材质先计算 NormalMap 的 shading normal，并保存独立三角形 geometric normal。
最终 standard 光照复用同一 shading normal。unlit 用户材质声明其参与结果。
采用此 entry 时框架补 `Present` 标记；已有用户 shader 沿有效 opaque depth 继续提供默认接收能力。
材质分箱的全屏 GPU 遍历先为当前视图的 primary 和额外层写入完整空 receiver 数据，再由用户
receiver entry 填写有效像素。背景、透明覆盖和空材质分箱通过同一遍历形成当帧的空参与状态；
阴影请求与最终光照读取这次生成的相同数据。

请求消费用户声明的 shading normal，按原版方向/局部发光面背光条件处理，subsurface、hair 和
多 normal basis 通过对应 flags 表达背光资格。geometric normal 留作用户和独立参考的几何语义，
原版当前投射 caller 使用 shading normal；`VirtualShadowMapProjection.usf:122–143` 的几何估计
helper 在该固定源码中标为 unused。first person 请求采用默认 bias2 与 absolute minimum level8。
`SetAdditionalReceiverInputs` 接受用户 shader 在既有图中生成的 water/translucency/hair 层，
每层按 camera pixel 顺序提供同一32字节数据，depth 采用当前 camera projection。
用户 receiver entry 也可调用 `WriteAdditionalSurfaceReceiver` 写入三层，由同一材质主要持有者
登记其资源使用并维护额外缓冲；默认 opaque 使用一个空数据记录。额外层实际写入时按
water、front translucency、hair 顺序存放，每层包含所有 primary views 的 camera pixels。
`TrySurfaceReceiverPreviousPixel` 消费包含实际 AA 偏移的 current-to-previous clip 矩阵和前帧 UV 边界，
`TryReadSurfaceReceiverPreviousDepth` 消费用户解码的覆盖值，保持原版 front-layer 历史 depth 输入语义。
water/translucency 请求方向光，hair 同时沿实际 clustered local light 列表产生局部请求。
公开用户 shader 用例在 opaque 声明无 lighting 后，当前 water depth、AA 重投影的前帧 translucency depth
和 hair depth 继续保留其世界点覆盖，实际 `[0,0,0,0]→[1,1,1,0]`。原版额外深度来源为
`VirtualShadowMapPageMarking.usf:394–477`，shader 输出与 C# DTO 共用一个32字节布局。
方向与局部页边界参数默认 `.05`，typed view 的原 padding 持有对应值，保持384字节视图记录。
原版核对范围为 `VirtualShadowMapPageMarking.usf:349–414,480–612` 与
`VirtualShadowMapPageMarking.ush:82–143`。

## 相对世界坐标与 clipmap 地址

零 ray 的直接比较采用原版几何 slope allowance：在反向深度中取
`2 * max(0, slopeUV · texelCenterOffsetUV)`，并按100厘米的投影深度尺度限制。
当前 forward-depth 表示使用等价的 `2 * max(0, -gradientUV · offsetUV)`。
倾斜平面独立例的 receiver depth `.65`、stored depth `.5`、UV slope `-.4`、
texel offset `.25` 产生 allowance `.2`，实际 public shader sampler 从 shadowed 到 lit。
该片 TRX 与来源哈希保存在仓库外 `rg70-delivery-evidence/ticket-143/native-slope/`。

`WorldOrigin` 是整个 Game world 的 double 原点，实例、相机与光源位置共用其局部 float 空间。
`RenderView.WorldOrigin` 经唯一提取通路取得该原点。Cluster 816 字节视图记录携带各视图的近端
high/low 原点、当前与历史平移；用户位置继续采用 Game 原点的 float offset，投射时先进入
该视图的近端坐标。阴影 384 字节视图记录采用每层 clipmap 或局部灯的近端原点，保存
对应投射与预计算 UV 法线变换。请求、光栅、HiZ 和采样消费同一坐标关系。
世界原点迁移时，阴影历史位置与深度中心换算到当前数据空间，绝对页地址及实际内容变化
决定缓存更新。成功提交保存该帧原点；取消继续保留前次提交的数据。

方向 clipmap 用 double 光空间位置与 int64 网格 snap 建立绝对页地址；`WorldPageOriginX/Y`
保留 64 位地址，`VirtualPageOriginX/Y` 保存其低 32 位环形页 tag。建立 float 投影前先在
double 中减去原点的光空间位置。`ClipmapCornerRelativeOffsetX/Y` 以最粗层 snap 网格为公共
参考点，各自使用当前层 radius 为单位。当前 LookAt 与 UV 轴下的 corner 为
`snapLi - snapLn * 2^levelDelta + 2`。层间 UV 偏移为 `.25 * (cornerB - scale * cornerA)`，
其中 `scale = 2^-levelDelta`；层间深度偏移由各层投影的 M43 表达。

原版 UE 5.8.3 `396c9f05` 的 radius 为 `2^(absoluteLevel+1)` 厘米；引擎由
`WorldUnitsPerCentimeter` 转为世界单位，默认每厘米 `.01` 世界单位。公共原点使用世界单位。
原版核对范围为 `NaniteShared.cpp:231–284`、`VirtualShadowMapClipmap.cpp:337–391` 和
`VirtualShadowMapProjectionCommon.ush:77–91,218–305`。远原点 `2^40+1/8` 保留 `-1/2048` 的独立期望。
近、远原点的公开场景保持 `1/8` 遮挡者间距，raw Half 位与 beauty 像素逐位一致。
实际证明由 [#70 验收记录](../../../docs/agents/acceptance-70.md) 统一索引。

阴影视图每帧通过专用当帧区段，从上传页拷贝到图创建的设备缓冲。
工作反馈在这一缓冲写入当前 bias，请求、几何视图压缩和采样继续绑定同一视图输入。
图的持久主体保留拓扑，当帧区段维护近每帧整体更新的视图数据与上传工作。

## 跨帧页面顺序

`MarkPagesUsingFroxels` 默认 false。启用后，从主几何最终 HiZ 的 mip0 取得深度，
在8×8单元内按原版对数透视 slice 或线性正交 slice 去重，生成8字节 packed XY/Z。
128字节参数由同一接收视图取得投影、相机逆视图、tile 标度、近端及深度 slice 标度。
视图空间 AABB 经阴影视图 frustum 裁剪后按覆盖像素中心生成矩形，方向与局部光
继续共用当前视图、光列表、DOF/分辨率偏移、页请求和接收单元掩码。
水层采用方向光 froxel 请求；毛发和第一人称接收面继续使用像素资格路径。
`HighQualityFrontLayerTranslucency` 和额外接收输入的 translucency 事实选择全视图像素请求。
frame 区段生成坐标与计数，持久请求阶段消费其 indirect args；coarse 需求沿原有阶段合并。
原版依据为固定提交的 `Froxel.cpp`、`Froxel.ush`、`FroxelBuild.ush`、
`VirtualShadowMapArray.cpp:138–143,861–866`、`VirtualShadowMapPageMarking.usf:78–202,613–781`
与 `NaniteHZBCull.ush:71–101`。来源 Git blob/SHA-256/字节数与直接结果位于
仓库外 `F:/SomeEngine.AgentWork/rg70-delivery-evidence/ticket-148/froxel/`。

标准材质用用户表面着色法线执行阴影偏置和投射。偏置长度取相机距离除以投影横向标度，
乘 `NormalBias`，下限为 `.02` 原版厘米。方向光完整光源角直径与局部光世界单位半径
沿 `LightShadowSettings` 提取到同一384字节视图。投射结果带有效性、可见比例和遮挡距离；
ray 数为零时使用带坡度容差的深度比较。方向和局部 SMRT 使用平方步长、深度历史、
受限斜率外推与波内自适应停止，点光射线在起终点所在 cube face 间切换。
接收投影保存相机位置和投影横向标度，页面需求与材质采样消费同一提取结果。
原版依据为固定提交 `396c9f059903aed5fec78ecd3d437a40c6415368` 的
`VirtualShadowMapProjectionCommon.ush`、`VirtualShadowMapProjectionSpot.ush`、
`VirtualShadowMapProjectionDirectional.ush` 与 `VirtualShadowMapSMRTTemplate.ush`。

SMRT 的随机输入由阴影设置资产引用蓝噪音纹理，经现有 `Texture` 和 `RenderDevice`
路径上传。标量取红通道，两个向量通道取绿蓝通道；像素与帧号按实际纹理维度包裹，
frame slice 按纵向行排列。R2 偏移选择每条射线的向量样本，方向光使用同一标量步长，
局部光按原版 `EvolveSobolSeed` 更新后续射线步长。默认资产来自 Christoph Peters
的 CC0 三维 void-and-cluster 数据，由 [Calinou 分发](https://github.com/Calinou/free-blue-noise-textures)
的固定提交保存为紧凑 RGBA8。实际尺寸为64×64×64，原版纹理输入为128×128×64的
spatiotemporal blue noise，维度和统计性质分别记录于 `assets/Noise/source.json`。
完整许可、来源 Git blob、源数据和转换结果的 SHA-256 保存在同目录。

默认方向和局部 ray 数均为7，采样步数均为8。公开 `--vsm-ray-count` 同时设置两类
ray 数，0选择硬深度比较；`SceneScript.ShadowSettings` 提供逐类 ray 数和原版参数。
DefaultGame 的硬阴影源三角形参考明确使用0；软阴影采用光源面上的独立射线积分真值。

局部光的最小 mip 使用光包围球最近深度、接收相机像素半径和光投影标度计算，
并消费同一局部 mip 奇偶模式。`DistantLightMode` 默认 `Conservative`，保守估计到达末级时
使用 `SinglePage` 表示；`All` 对所有局部光采用该表示。`MaximumDistantUpdatesPerFrame` 默认一。
完成帧以光实体保存上次计划更新的帧号，较旧光源先取得预算；查询和取消继续保留完成帧年龄。
光移动、投影或缓存代号变化使该光立即重绘。留用的远距离光页保留动态深度，遮挡者事件由
下一次预算更新消费。原版来源为 `ShadowSceneRenderer.cpp:218–259,343–429` 与
`VirtualShadowMapCacheManager.h:109,369–410`。

方向光预滤由 `PrefilteredDistantProjectEnable` 与 `PrefilteredDistant` 两个设置共同启用，
两者默认 false；历史权重默认 `.87`。启用后 regular clipmap 采用原版缩紧的远端范围，
每帧刷新其深度，并在同一384字节视图的 `DepthCullRange` 保存相邻层的深度分界。
细页接收需求按整数 corner 地址将8×8接收单元压缩到所有较粗层，并保持 secondary/detail
需求和接收覆盖。两轴 `[1,4,6,4,1]/16` 过滤消费实际完成的深度，缺少邻页时使用当前页边缘。
历史从当前分界中点重建世界位置，投影到同光源的前帧视图，确认前帧页与接收覆盖后混合；
变化幅度以原版 `1/255` 最小步长推进。独立 R8 页带一个 texel 边框，较粗层的过滤遮挡相乘
形成当前层结果，随后更新合并页边框供材质双线性采样。两份持久图历史随成功完成的帧切换，
取消继续使用前次完成层。全局页压力通过完成帧回读进入保守局部 mip，沿实际请求使用的同一 bias。
原版依据为 `VirtualShadowMapClipmap.cpp:443–475`、`VirtualShadowMapPageManagement.usf:403–495` 与
`VirtualShadowMapPhysicalPageManagement.usf:1091–1577`。

页池以 GPU `ShadowPageLists` 的 LRU、AVAILABLE、EMPTY、REQUESTED 四个列表维护跨帧顺序。REQUESTED 在帧结束时包含全部图集页；下一帧先保存该顺序，再将当前请求页移入 REQUESTED，未请求页保序压缩进入 AVAILABLE，空页追加在 AVAILABLE 尾部并优先分配。分配后的剩余 AVAILABLE 追加到本帧请求页之后，成为下一帧输入。未请求缓存页上的完成状态和失效事实随原页面保留，后续请求或回收负责消费。准备、压缩和收尾由持久图中的显式阶段表达，帧取消、部分提交和重新初始化沿现有完成边界维护。

几何流送的 GPU 页请求携带实际页范围、投影误差优先级和视图类别，主视图类别3、阴影类别0。每页保留最强需求，独立收尾操作将该值写入同一读回记录；CPU 对需要加载的页一次展开依赖并按优先级选择读取，`MaxConcurrentPageLoads` 控制并发读取数。驻留系统持有质量值，初值1，完整反馈按真实可见流送页占页堆容量的比例推进：超过85%连续两帧后每帧乘.97，低于70%连续30帧后每帧乘1.01，范围.3到1。页长度可变时使用页堆分配字节，固定页长度时等价于原版页数比例；最低细节页与其容量从流送部分分离。截断反馈保留当前质量，后续完整需求继续推进。

BVH 的 `ChangedMeshes`、材质绑定的变化实体以及实际纹理通知形成同一份深度变更记录，VSM 按当前实例行投影到已有页。记录由完成的帧确认，当前与上一帧 bounds 中的 static/dynamic 分类分别消费。几何流送的 LOD 变化在同一实例缓冲尾部表达，并在 GPU 页 metadata 中保留 deferred 标记；`DeferredInvalidationBudget` 默认−1，0保留标记，正数按每帧页数消费，剩余标记随页保留到后续请求。材质 alpha、标量及调用方每帧报告的变形继续即时失效。有限 bounds 使用局部范围，范围不完整及变更历史溢出沿整体缓存保护处理。

原版依据为固定396c9f05的 `NaniteStreaming.ush:19–31`、`NaniteStreamingManager.cpp:476–507/2752–2760`、`VirtualShadowMapArray.cpp:119–127` 和 `VirtualShadowMapPhysicalPageManagement.usf:321–379`。视图工作量从包含 policy 位的 draw view index 转成 compact 数组索引后统计。

第一人称分类由 `MeshInstance.FirstPerson` 沿 Mesh 提取进入同一实例属性列。8字节 `FirstPersonMeshData` 保存类别、玩家编号和调用方可见性，`RenderView.PlayerId` 选择当前玩家；`SceneCamera` 与场景序列化承接这些输入。`FirstPerson` 几何进入主视图并使用相机 FOV/scale 变换，玩家可见性由调用方设置。`WorldRepresentation` 为玩家的世界替身：玩家自己的主视图与普通阴影视图隐藏，独立第一人称阴影视图接收；其他玩家按普通场景对象读取。主视图、普通阴影与第一人称阴影通过同一视图的 `Family` 选择实例。

第一人称相机矩阵保存在 Cluster 视图中，包含当前、逆向与前帧变换。变换作用于用户 shader 返回的位置，三角 domain 的重建和运动矢量使用对应投影；用户声明的接收面法线与内置标准材质的世界位置消费同一变换。第一人称阴影与普通 clipmap 共用 caster 数据、页池与图集，使用独立的页地址范围与视图 family。默认第一人称层级8–18，细页需求由 receiver 标记，每帧刷新深度。世界接收面合并两份有效投射结果的最暗遮挡，第一人称接收面消费普通世界阴影。玩家编号变化、取消和完成沿已有帧边界接合。

接收面 producer 位于主可见性之后、页需求之前的固定持久图区段；主层和额外层先清零，再执行用户材质。原版依据为固定396c9f05的 `PrimitiveSceneProxy.cpp:631–648`、`ShadowSceneRenderer.cpp:76–88/565–636`、`SceneView.cpp:742–757` 与 `VirtualShadowMapProjection.usf:437–485`。

局部光 receiver 背光容差将原始光向量和光源半径共同换算为厘米，角度余量按 radius/sqrt(distanceSquared+1) 形成。方向光 froxel 根据距离、global/extra bias、逐VSM bias选择 clipmap，并在绝对 level 最低0后换成对应视图索引；包围体与投影 frustum 的相交关系形成实际请求。中心点的Z位置用于选择的空间距离，最终接收覆盖由包围体裁剪决定。依据固定396c9f05的 VirtualShadowMapPageMarking.ush:18–40、VirtualShadowMapPageMarking.usf:176–203 和 VirtualShadowMapProjectionSpot.ush:250–260 与 VirtualShadowMapPageMarking.usf:605。

方向光caster的深度范围按原版作为写入深度的投影数据，HW覆盖使用z=.5w并独立传递原始clip.zw；PS普通perspective插值后由z/w取得实际深度。主相机与局部光形成depth-clip条件，方向光的空间剔除与SW/HW光栅消费同一显式条件。compact阶段是目标条件的生产者，视图/目标字段沿既有GPU数据进入消费者。
