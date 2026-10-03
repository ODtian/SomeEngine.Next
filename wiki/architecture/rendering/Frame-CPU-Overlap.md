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

prepare 作为写入权参数交给提取应用、实例准备和关闭接口；释放即结束写入。
`RenderFrameSystems.Update(readScope, graphFrame)` 把共享读取权限交给帧消费系统。
应用在录制与提交结束后释放读取，下一轮 prepare 即可取得独占访问。

| 活跃 CPU 作用域 | 开始 prepare | 开始读取 |
| --- | --- | --- |
| 空闲 | 成功 | 成功 |
| 一个 prepare | 冲突返回失败 | 冲突返回失败 |
| 一个或多个读取 | 冲突返回失败 | 成功，与现有读者共存 |

这组接口的访问状态只由活跃 CPU 作用域决定。`RenderDataLockTests` 使用 WARP，把图形队列
阻塞在显式外部同步点上，验证 GPU 完成前后都具有相同的读写互斥行为。

## Frame storage

`RenderGraph.TryOpenNextFrame` 选定帧资源并等待该帧的队列完成点。实例准备直接写入
`RenderGraphNextFrame` 的映射页，提交结构编辑后通过 `BeginFrame(nextFrame)` 开始录制。
ImGui 顶点、索引与变换常量通过 `RenderGraphFrame.Upload` 写入当帧映射空间，图负责
这些字节的在途生命周期。详见 [[architecture/render-graph/Frame-Uploads]]。

实例、ImGui 与 Cluster 使用图的帧资源。Cluster 的上传、回读和 HiZ 生命周期由图管理。
Runtime 的开帧资源等待统一到 `TryOpenNextFrame(next, timeout)`；读取与 prepare 只尝试取得
CPU 权限。图持有唯一 CPU 帧及其上传页、回读页和 GPU 完成点。

帧输出回读位于组合根中 UI 之后的当帧区段。帧输出验收与同步帧选项在提交后显式等待 GPU，
关闭阶段排空实际提交，再在 prepare 中释放业务资源。设备回收职责见 [[RenderDevice]]。

## Relations
[[Extraction]] · [[Delayed-Feedback]] · [[RenderDevice]]
