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

clipmap 按二次幂分级。默认偏移为零时，可覆盖目标位置的选中级别使像素投影的最短主轴
覆盖 1–2 个阴影 texel；较长主轴按投影比例覆盖更多 texel。虚拟分辨率的级别范围限定最大精度。
页池容量不足或者目标页还没有驻留时仍可能采样较粗的页，不能把回退画面视为目标分辨率。

页原点沿实际光视图的 X/Y UV 方向滚动。相机跨过对齐边界时，固定世界几何的细页地址保持不变。
clipmap 每次滚动四分之一页表；无法按整个 mip 页对齐的较粗 mip，在投影变化时重新绘制。
请求范围也覆盖材质的法线偏移和过滤半径，包括相邻页和相邻接收单元，避免采样落在未绘制区域。

`VirtualShadowReceiverProjection` 的 GPU 参数占 96 字节；28 字节的 `CameraDepthOfField`
后保留 4 字节填充，使 `CameraPosition` 和 `CotanHalfFieldOfView` 分别位于字节 80、92。
该包装结构与 `vsm_receiver_projection.slang` 共用字段顺序，请求与材质采样读取同一份参数。
