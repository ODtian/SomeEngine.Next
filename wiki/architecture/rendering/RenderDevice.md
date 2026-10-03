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

## Submitted work and resource replacement

`TrackSubmission` 登记实际提交的队列完成点，同一队列保留最大值。
Runtime 在图执行成功后直接登记返回的完成点；部分提交的失败路径登记已接受的完成点。
`RetireAfterTrackedSubmissions` 将替换资源关联到调用时已登记的提交；
`CollectCompleted` 在相关队列完成后释放资源。调用方停止资源的后续使用，再登记其回收。
正常帧在打开下一帧后调用 `CollectCompleted`，同时回收完成的替换资源。
关闭阶段使用 `WaitForTrackedSubmissions(timeout)` 排空设备上已登记的工作，再取得 prepare
写入权执行业务资源关闭；释放 prepare 后完成业务对象的销毁。

CPU 数据访问由 [[Frame-CPU-Overlap]] 中的 `RenderDataLock` 表达；图的上传页、回读页与帧
完成边界由 RenderGraph 持有。GPU 完成只控制资源复用与回收，CPU 读写权限只由作用域控制。

## Relations
[[architecture/assets/Texture-Device-Realization]] · [[architecture/render-graph/Resource-Access]]
