# Frame CPU Overlap

## Definition
GameWorld 下一轮 CPU 工作可以与已经交付给渲染侧的上一轮 CPU/GPU 工作重叠。

## Invariants
- render prepare 完成后 renderer 不再依赖该轮 GameWorld 的活跃访问。
- Game N+1 不需要等待 Render N GPU 完成。
- 需要设备反馈的决策只能作用到未来 frame。

## CPU data access

`RenderDataLock` 提供两种 CPU 访问：`TryBeginPrepare` 取得独占的
`RenderPrepareScope`，`TryBeginRead` 取得共享的 `RenderReadScope`。prepare 与读取冲突时
返回 `false`；多个读取可以同时存在。作用域释放后，后续访问可以重新取得对应权限。
帧录制、诊断与捕获使用同一种共享读取权限。

这组接口的访问状态只由活跃 CPU 作用域决定。`RenderDataLockTests` 使用 WARP，把图形队列
阻塞在显式外部同步点上，验证 GPU 完成前后都具有相同的读写互斥行为。

## Frame storage

`RenderGraph.TryOpenNextFrame` 选定帧资源并等待该帧的队列完成点。实例准备直接写入
`RenderGraphNextFrame` 的映射页，提交结构编辑后通过 `BeginFrame(nextFrame)` 开始录制。
ImGui 顶点、索引与变换常量通过 `RenderGraphFrame.Upload` 写入当帧映射空间，图负责
这些字节的在途生命周期。详见 [[architecture/render-graph/Frame-Uploads]]。

实例、ImGui 与 Cluster 使用图的帧资源。Cluster 的上传、回读和 HiZ 生命周期由图管理，
应用通过 `TryOpenNextFrame` 选择并等待图帧资源。现有协调器的 CPU 访问使用方继续统一迁入
`RenderDataLock`。

## Relations
[[Extraction]] · [[Delayed-Feedback]] · [[RenderDevice]]
