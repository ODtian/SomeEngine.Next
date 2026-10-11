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

材质纹理按唯一对象准备一次，相关 shader 输入共享这次 SRV/UAV 和上传依赖。
持久图连接通过 `RetainTexture` 持有设备分配；连接移除后，图在此前提交完成时释放该引用。
纹理绑定增删或 Texture `Changed` 才刷新输入。初次准备和替换上传保留在待刷新集合，
直到上传事务完成；取消或部分提交后的下一帧继续准备同一连接。稳定帧沿用图内资源与输入。
当帧准备的纹理由一个纹理帧对象保护并统一登记提交完成点；取消、替换和 GPU 完成沿该边界回收。
已登记纹理的提交末态由图维护，设备持有其只读状态引用；跨图首次使用交付该引用中的边界，
当帧导入资源的边界在设备侧保存至后续登记。
纹理准备直接复用本帧取得的状态引用；完成处理按当帧上传事务登记上传结果。

## Relations

应用的呈现 pass 通过 `OrderTextureUploadsBefore` 排在当帧纹理上传完成之后，缺页反馈的 GraphReadbackId 通过图上的显式 OrderAfter 依赖先于呈现完成。呈现与当帧图像读回由同一次交换链图像提交覆盖；首个场景帧沿这一公共路径验收。

[[architecture/assets/Texture-Device-Realization]] · [[architecture/render-graph/Resource-Access]]
