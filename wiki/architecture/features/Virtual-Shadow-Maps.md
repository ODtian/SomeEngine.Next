# Virtual Shadow Maps

VSM 是独立 rendering feature，拥有自己的 page table、atlas/cache、marking、allocation 与 shadow policy。

VSM 构造普通 unified render views，并通过 geometry pipeline 能力使用 Cluster；Cluster 不知道这些 views 表示 shadow。VSM counters 和策略不进入 Cluster core ABI。

参见 [[architecture/rendering/Multi-View]] · [[architecture/cluster/Cluster-Scope]]。

方向光的 clipmap 与局部光的 mip 都根据屏幕像素投影到光空间后的覆盖范围选择。
两个屏幕像素偏移形成光空间中的像素投影，选择级别使用其最小主轴宽度。
该宽度由投影矩阵的较大奇异值与行列式计算，覆盖拉伸、斜切及视图旋转后的短轴精度。
请求页与材质采样使用同一选择函数；材质调用 `SampleDirectionalVirtualShadowMap` 时，
在世界坐标位置后传入接收面上 X/Y 相邻屏幕像素的世界坐标。
页请求与标准材质共用 `ReconstructVirtualShadowReceiverPixel`，从当前深度图
重建接收面相邻像素的世界坐标。每个轴采用深度差较小的前景邻居，在断层处
保持局部接收面；视口边缘沿该接收面延伸相邻坐标。
`DirectionalResolutionLodBias` 只表示用户指定的精度偏移，不再混入相机投影倍率。

方向光通过 `SelectDirectionalVirtualShadowView` 选取接收者投影。它先按像素覆盖范围
计算目标级别，再寻找覆盖该世界位置的首个 clipmap。请求页与材质采样共用这一选择。
动态分辨率的页池压力偏移作用于这个覆盖投影，使压力反馈能降低实际请求页的密度。
DefaultGame 启用此反馈；页分配失败、页池占用和光栅工作量由 VSM 自身维护。
`DynamicResolutionTargetRasterWork` 的单位为 8×8 页材质 batch 等价量：HW 使用
裁剪后 page-batch 面积 / 64，SW 有界几何 batch 使用一。它不是候选数、窗口数或
indirect instance 数；将 HW 窗口改为四页不能单凭分区数增加而降低分辨率。
当前裁剪面积反馈可比旧的每窗口一更低，可能恢复更多细节；最终有效质量/成本仍需实测。

clipmap 按二次幂分级。默认偏移为零时，可覆盖目标位置的选中级别使像素投影的最短主轴
覆盖 1–2 个阴影 texel；较长主轴按投影比例覆盖更多 texel。虚拟分辨率的级别范围限定最大精度。
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
参见 [[architecture/validation/Vsm-Depth-Coverage-135]]；当前只有源码/构建检查，GPU 验收待完成。

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
