# RenderDevice

## Definition
RenderDevice 是一个 Graphics.Device 对应的 render-side 长期设备状态，位于 SomeEngine.Render。

## Responsibilities
- per-device asset realization
- Texture SRV/UAV cache
- streaming uploads
- backing replacement
- device-side missing resources
- 已提交工作结束后的旧 Graphics 资源回收

## Invariants
- RenderDevice 与 RenderGraph 正交；两者都建立在 Graphics 之上。
- RenderGraph 不认识资产 Texture 或 RenderDevice。
- RenderDevice 不包含 RenderGraph；同一 Graphics.Device 可以服务多张图。
- feature 先显式取得真实 Graphics 资源，再显式交给 RenderGraph 声明访问。
- 长期集合持有 GPU 记录；业务 Texture 由普通引用、当帧工作、上传事务和完整更新各自按实际使用期持有。
- TextureDevices 的 GC 收尾提交设备释放请求，CollectCompleted 在渲染线程处理队列。已登记帧与实际 GPU 完成共同保护旧分配和视图。
- 设备关闭与更新登记、GC 入队在设备记录上原子交接；关闭在锁外清理 Texture 关联并推进更新完成，随后收尾全部设备记录及请求。

## Relations

应用的呈现 pass 通过 `OrderTextureUploadsBefore` 排在当帧纹理上传完成之后，并通过 Cluster 的 `PageFaultReadbackPass` 排在缺页反馈的提前提交之后。呈现与当帧图像读回由同一次交换链图像提交覆盖；首个场景帧沿这一公共路径验收。

[[architecture/assets/Texture-Device-Realization]] · [[architecture/render-graph/Resource-Access]]

图帧提交完成后，`RenderGraph.CollectCompleted()` 或后续 `BeginFrame` 推进帧登记回收并清除帧级 Texture 引用；`RenderDevice.CollectCompleted()` 消费设备释放队列，沿已登记的 GPU 完成位置释放原生纹理和视图。业务在结束消费时按这两个各自的完成入口推进收尾。
