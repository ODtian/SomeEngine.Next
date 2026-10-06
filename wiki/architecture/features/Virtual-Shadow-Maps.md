# Virtual Shadow Maps

原始输出已移入仓库外验收归档；文中的 `artifacts/acceptance/` 路径对应归档内目录。归档身份与结果摘要见 [#70 验收记录](../../../docs/agents/acceptance-70.md)。

VSM 是独立 rendering feature，拥有自己的 page table、atlas/cache、marking、allocation 与 shadow policy。

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
由实例几何 producer 写入共享实例存储中的单个 uint 列。默认 Dynamic，建筑导入声明 Static。
用户的动画 vertex、domain 或 opacity 输入可声明 Dynamic；静态输入修改沿既有 caster/material 事件失效。
当前和上帧分类随既有 world bounds 的 W 分量传给失效 shader，旧、新足迹各按实际分类更新。
静态输入失效清除静态完成代号并刷新两类深度，动态输入失效保留静态完成代号。
PageMetadata.y 保存完整静态深度及静态 HiZ 的完成代号；W 持有两类重绘和失效标记。
静态 atlas 与动态/合并 atlas 共用页编号。绘制前，静态可留用页为动态纹理提供初值；
静态失效页从零开始。SW、HW、mesh 和有限 tessellation 输出共同消费同一实例分类。
静态光栅覆盖整页，动态光栅消费本帧接收者遮罩。main/post 绘制后的静态 merge 先于对应 HiZ。
遮罩页每帧保持 dynamic uncached，接收掩码扩张从留用静态深度重新形成当前动态内容。
共享 coarse 模式 2 的 ForceCacheDynamicCoarse 标记使仅 coarse 请求页留用 dynamic，
detail 请求和 static 失效仍触发所需更新。未请求的已分配页保留其失效标记直到再次请求。
质量回读分别给出 static/dynamic 的 HW/SW 可见 cluster/window；这些计数沿原版可见项边界产生。
接收者、过滤、完成有效性共用 `vsm_receiver_projection.slang`、`vsm_sampling.slang`，
generic raster adapter 共用 `cluster_policy_raster_output.slang` 与 `cluster_raster_target_output.slang`。

完成深度后，`PropagateVirtualShadowResidentPages` 从主要页表产生当帧 resident 引用。
每个引用保存已完成来源的表索引和 LOD offset；本级 rendering 映射继续由主要页表维护。
Local 引用指向同一投影的父 mip，directional 引用指向同一光源更粗的 clipmap。
材质的 any-LOD 查询直接读取已传播引用，本级查询读取原始完成映射。
遮罩来源的每个采样 tap 验证其当前接收单元；覆盖不足时沿下一驻留祖先继续解析。
引用随当帧主要页表的完成数据重建，图完成边界位于传播之后，取消与部分失败沿现有状态恢复。
Directional 页地址采用 `quarterPages * corner` 的整数移位，UV 使用
`.25 * (cornerB - scale * cornerA)`，其中 `scale = 1 / 2^levelOffset`。
coarse 深度以 `(coarseDepth - (offsetZB - scale * offsetZA)) / scale` 返回 requested 范围。
过滤使用同一 coarse 投影的 UV、深度和接收平面梯度。
原版依据为 `VirtualShadowMapPageManagement.usf:268–401`、
`VirtualShadowMapPageAccessCommon.ush:355–371` 与 `VirtualShadowMapProjectionCommon.ush:245–305`。

HiZ 使用静态深度的一个图管理持久存储：main 读取先前结果后，intermediate 更新脏页，
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

## 相对世界坐标与 clipmap 地址

`WorldOrigin` 是整个 Game world 的 double 原点，实例、相机与光源位置共用其局部 float 空间。
`RenderView.WorldOrigin` 经唯一提取通路取得该原点，Cluster 560 字节视图记录携带当前和历史
high/low 原点，阴影 272 字节视图记录携带同一 high/low 原点。请求、几何、光栅、HiZ 与采样
消费此局部空间中的矩阵和位置。原点变化使下一帧采用新的主视图历史起点与阴影缓存代号，
成功完成提交后保存新原点；取消继续保留前次提交的原点。

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

局部光的最小 mip 使用光包围球最近深度、接收相机像素半径和光投影标度计算，
并消费同一局部 mip 奇偶模式。`DistantLightMode` 默认 `Conservative`，保守估计到达末级时
使用 `SinglePage` 表示；`All` 对所有局部光采用该表示。`MaximumDistantUpdatesPerFrame` 默认一。
完成帧以光实体保存上次计划更新的帧号，较旧光源先取得预算；查询和取消继续保留完成帧年龄。
光移动、投影或缓存代号变化使该光立即重绘。留用的远距离光页保留动态深度，遮挡者事件由
下一次预算更新消费。原版来源为 `ShadowSceneRenderer.cpp:218–259,343–429` 与
`VirtualShadowMapCacheManager.h:109,369–410`。

页池以 GPU `ShadowPageLists` 的 LRU、AVAILABLE、EMPTY、REQUESTED 四个列表维护跨帧顺序。REQUESTED 在帧结束时包含全部图集页；下一帧先保存该顺序，再将当前请求页移入 REQUESTED，未请求页保序压缩进入 AVAILABLE，空页追加在 AVAILABLE 尾部并优先分配。分配后的剩余 AVAILABLE 追加到本帧请求页之后，成为下一帧输入。未请求缓存页上的完成状态和失效事实随原页面保留，后续请求或回收负责消费。准备、压缩和收尾由持久图中的显式阶段表达，帧取消、部分提交和重新初始化沿现有完成边界维护。
