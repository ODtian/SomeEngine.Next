# Virtual Shadow Maps

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
动态分辨率的页池压力偏移作用于这个覆盖投影，使压力反馈能降低实际请求页的密度。
DefaultGame 启用此反馈；页分配失败、页池占用和光栅工作量由 VSM 自身维护。
`DynamicResolutionTargetRasterWork` 的单位为 8×8 页材质 batch 等价量：HW 使用
裁剪后 page-batch 面积 / 64，SW 有界几何 batch 使用一。它不是候选数、窗口数或
indirect instance 数；将 HW 窗口改为四页不能单凭分区数增加而降低分辨率。
当前裁剪面积反馈可比旧的每窗口一更低，可能恢复更多细节；最终有效质量/成本仍需实测。

clipmap 使用原版绝对级别，默认 first 6 至 last 22。级别 L 的半径为
`2^(L+1) * WorldUnitsPerCentimeter`，投影为容纳滚动对齐覆盖四个半径。
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

HiZ 只有一个图管理的持久存储：main 读取先前结果后，intermediate 更新脏页，
post 消费已保留的 deferred work，再更新 final 供下帧使用。不复制整池历史，
不 replay 或重新从根发现 deferred work。未受影响的缓存页不复制或重建。
参见 [[architecture/validation/Vsm-Depth-Coverage-135]]；已有分项公开输出/局部光 RT 检查，
完整 GPU 验收仍被 native testhost 中止及未完成的默认三场景/错误版本回归等门禁阻挡。

页原点沿实际光视图的 X/Y UV 方向滚动。相机跨过对齐边界时，固定世界几何的细页地址保持不变。
clipmap 每次滚动四分之一页表；无法按整个 mip 页对齐的较粗 mip，在投影变化时重新绘制。
请求范围也覆盖材质的法线偏移和过滤半径，包括相邻页和相邻接收单元，避免采样落在未绘制区域。

遮挡者变化同时使旧包围盒与新包围盒覆盖的缓存页失效，遍历该光源投影的全部 mip。
末级方向光 clipmap 的最粗 mip 提供兜底深度，随遮挡者变化一起刷新。

阴影比较在实际 atlas texel 中心计算接收面深度。接收位置与相邻像素的光空间投影
形成接收面深度梯度，硬阴影、遮挡者搜索与过滤采样共用此梯度。默认 `DepthBias`
为 `0.000001`，在默认方向光深度范围中对应约 1 mm；`NormalBias` 为 `0.001` 世界单位。
用户材质标量和已有 texture 内容通知同样使深度失效，即使资源绑定/光栅箱不变。
几何驻留变更、主相机几何选择变更、外部有界/无界输入失效入口继续保留。

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
当前深度代号继续消费几何驻留与材质输入 revision；局部几何/材质失效在 #147 与静态/动态缓存
#141 中接合每个遮挡者的实际影响范围。
