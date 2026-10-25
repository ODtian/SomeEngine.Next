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
- 长期集合持有 GPU 记录；业务 Texture 由普通引用、当帧工作、上传事务和完整更新各自按实际使用期持有。设备记录到 Texture 的反向关联是弱引用，不通过设备缓存无限保活业务对象。
- TextureDevices 的 GC 收尾提交设备释放请求，CollectCompleted 在渲染线程处理队列。已登记帧、图连接引用与实际 GPU 完成共同保护仍在使用的分配和视图；释放请求不能越过这些边界直接销毁资源。
- 设备关闭与更新登记、GC 入队在设备记录上原子交接；关闭在锁外清理 Texture 关联并推进更新完成，随后收尾全部设备记录及请求。不恢复业务 Texture 的 Changed/Dispose 协议。

## Graph retention and texture preparation

材质纹理按唯一对象准备一次，相关 shader 输入共享本次 SRV/UAV 和上传依赖。持久图连接通过 `RetainTexture` 取得设备分配引用；连接移除后，在此前已提交使用完成时释放引用。图拥有持久资源连接，设备拥有原生分配与视图，不再添加第二套相同缓存。

纹理绑定增删、RenderWorld 提取的 `TextureVersions` 变化或尚未完成的上传使输入进入准备路径。材质属性计数没变不表示纹理内容没变；直接绑定与 indexed descriptor 使用同一提取版本来源，不依赖 `Texture.Changed` 事件。稳定帧沿用图内资源和输入。图编辑被接受后才发布新的管线纹理状态，失败或取消不能提前推进已接受版本。

当帧纹理准备由一个纹理帧对象持有实际使用对象与上传事务，并登记提交完成点。初次准备和替换上传保留在待刷新集合，直到上传完成；取消或部分提交后按真实结果处理已接受工作，下一帧继续准备未完成部分。提交末态由图维护，设备保留其状态引用供跨图或后续导入使用。

同一设备纹理分配只能由一个尚未完成的 CPU 图帧准备，包括没有新增上传的情况。其它帧须等原帧执行或取消完成后再准备该分配；此前 GPU 工作可以仍在途。内容替换的新分配具有独立帧状态，可以与旧分配的 CPU 图帧准备重叠。替换不会改变业务持有的 Texture 对象身份。

## Completion and shutdown

`TrackSubmission` 登记使用此设备的实际队列完成点，同一队列保留最大值，并拒绝其它设备的完成点。`RetireAfterTrackedSubmissions` 将替换资源关联到调用时已登记的提交；调用方必须先停止发布该资源的新使用。`CollectCompleted` 在相关提交完成后释放这些替换资源；`WaitForTrackedSubmissions(timeout)` 用于有界排空已登记工作，超时不是安全释放信号。正常和部分失败路径均须保留真正被接受的提交，而不是按计划中的提交推断完成。

图帧提交完成后，`RenderGraph.CollectCompleted()` 或后续 `BeginFrame` 推进帧登记回收并清除帧级引用；`RenderDevice.CollectCompleted()` 消费设备释放队列，沿已登记 GPU 完成位置释放原生纹理和视图。业务结束消费时须推进这两个各自的完成入口，而不是以 CPU 已返回、GC 已运行或一次提交成功替代 GPU 完成。

CPU prepare/read 权限与 GPU 完成是不同边界：前者由实际 frame/coordinator 作用域负责，后者控制资源复用与回收。关闭和失败排空必须保留已接受提交的完成点，不得为了清理绕过仍在途使用。

## Relations

应用的呈现 pass 通过 `OrderTextureUploadsBefore` 排在当帧纹理上传完成之后，并通过 Cluster 的 `PageFaultReadbackPass` 排在缺页反馈的提前提交之后。呈现与当帧图像读回由同一次交换链图像提交覆盖；是否完成首帧正确性或性能验收须由最终内容与实际运行证明。

[[architecture/assets/Texture-Device-Realization]] · [[architecture/render-graph/Resource-Access]]
