# Clustered Light Binning

`ClusteredLightBinning` 是光照网格使用 [Screen-Binning](Screen-Binning.md) 的独立入口。调用方在加载阶段传入已编译的 `clustered_light_binning` shader、设备和图，持久 pass 组随入口创建。每帧 `Record(ref frame, lights, views, cameraPosition)` 接收 Rendering world 提取的 `RenderLightSet` 与主视图；全部缓冲、上传和诊断读回由 RenderGraph 持有。

`RenderLightSet.OrderForUpload` 维护 point 与 spot 的统一上传次序：强度除以相机距离平方降序，相同时按实体索引、代数升序。当前全部光源参与 VSM，投影优先级处于同一组。方向光使用独立前缀；局部灯光连续上传，GPU 下标就是分箱优先级，`LightBuffer` 保留供后续着色使用的同一份输入。

每个分箱条目是一个局部灯光与视图的组合。Count 用灯光屏幕包围矩形和深度切片生成覆盖数量；公共扫描产生展开空间。Expand 将每个 cell 分给一个线程，先求 cell 的视空间 AABB，再执行球体与 AABB 相交。spot 在球体测试之后用 cell 包围球执行保守圆锥测试。Fill 使用共享有序插入，cell 内下标升序。

`ClusteredLightGridSettings.LightListCapacity` 表达全局引用总容量，`MaxLightsPerCell` 表达单 cell 上限。每个 cell 优先保留下标最小的灯光。全局容量按 cell 次序保留完整段；`LastFrameOverflowCells` 汇总单 cell 截断与总容量溢出，`FeedbackFrameIdentity` 记录来源帧。构造函数接收已有 `RenderPrepareSystems`，在自身图编辑成功提交后注册准备系统；该系统在 `TryOpenNextFrame` 交付读回之后、`BeginFrame(next)` 之前，根据 `RequiredListLength` 自动提交容量扩展。`Cells` 保存偏移和数量，`LightIndices` 保存灯光下标，`CompletePass` 提供下游排序边界。

公共参数 `ClusteredLightBinningView` 给出视图矩阵、投影矩阵、tile 与深度参数和逆投影矩阵，行为检查通过这些参数计算 CPU 参考 cell 集合。`LightingFeature` 持有此入口，应用 prepare 阶段在 `RenderLightSetSystem` 更新后建立一次上传顺序；Lighting、VSM 请求和材质着色共用这份灯光缓冲。VSM 保留按来源灯光分配的投影视图，`VirtualShadowLightViewIndices` 将上传下标映射到来源投影；优先级变化只改变映射。标准材质的前向阴影采样通过同一张表访问阴影。
