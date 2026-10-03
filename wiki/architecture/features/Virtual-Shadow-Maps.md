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

clipmap 按二次幂分级。默认偏移为零时，可覆盖目标位置的选中级别使像素投影的最短主轴
覆盖 1–2 个阴影 texel；较长主轴按投影比例覆盖更多 texel。虚拟分辨率的级别范围限定最大精度。
页池容量不足或者目标页还没有驻留时仍可能采样较粗的页，不能把回退画面视为目标分辨率。

页原点沿实际光视图的 X/Y UV 方向滚动。相机跨过对齐边界时，固定世界几何的细页地址保持不变。
clipmap 每次滚动四分之一页表；无法按整个 mip 页对齐的较粗 mip，在投影变化时重新绘制。
请求范围也覆盖材质的法线偏移和过滤半径，包括相邻页和相邻接收单元，避免采样落在未绘制区域。

遮挡者变化同时使旧包围盒与新包围盒覆盖的缓存页失效，遍历该光源投影的全部 mip。
末级方向光 clipmap 的最粗 mip 提供兜底深度，随遮挡者变化一起刷新。

阴影比较在实际 atlas texel 中心计算接收面深度。接收位置与相邻像素的光空间投影
形成接收面深度梯度，硬阴影、遮挡者搜索与过滤采样共用此梯度。默认 `DepthBias`
为 `0.00002`，在默认方向光深度范围中对应约 2 cm；`NormalBias` 为 `0.01` 世界单位。

三场景自动回归以硬件 DXR inline `RayQuery` 阴影为真值，参考使用源 glTF 的
BLAS/TLAS、实例变换、透明度遮罩与法线贴图。相机与方向光从同次场景资产读取，
材质输出的 VSM 可见性与 RT 遮挡结果逐像素比较。独立 CPU 三角形射线补充覆盖
局部光、遮挡者运动、接触阴影和方向光兜底页。测试入口为 `tools/Test-Shadows.ps1`。
参见 [[architecture/validation/Shadow-Reference-2026-10-04]]。
