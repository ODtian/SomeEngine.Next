# Resource Access

RenderGraph 的同步与生命周期来自 pass 对真实资源范围的 Read、Write、ReadWrite 声明。parameter binding 同时建立 shader 绑定和访问声明，descriptor 数值由绑定引用到对应资源。

渲染资源由 `RenderGraphEdit.CreatePersistentBuffer` / `CreatePersistentTexture` 创建，容量变化通过描述修改交给图处理。互操作资源通过 `RegisterExternalBuffer` / `RegisterExternalTexture` 注册一次，初始同步状态取自资源自身。后续帧状态、跨队列同步和旧分配退役均由图持有。

冷路径可用 `graph.GetBuffer(id)` / `GetTexture(id)` 创建长期 descriptor。帧内声明仍使用 graph ID、view 和 input。swapchain 图像通过 `frame.Import(in SwapchainImage, Queue)` 接入。

上传使用 `AddBufferUploadPass` 与 `frame.WriteBuffer`，或者 `frame.Upload` 的帧上传页。读回使用持久或当帧 `AddReadback`、`GraphReadbackId` 与 `IGraphReadbackReceiver`；帧读回与 pass 可通过 `OrderAfter` 建立依赖。读回页及映射只由 RG 管理。

帧输入由启用的消费方要求。禁用 pass 的输入访问在当帧资源连接中移除；其视图保留声明，并在输入提供且消费方开启后物化。pass 的启用变化重新连接它的视图、访问与参数绑定，已移除消费方留下的视图同样按这一路径处理。

读取未定义范围时，执行抛出公共 `UndefinedResourceReadException`，并按 [[Frame-Submission]] 进入 Lost 状态。

参见 [[Shader-Resource-Closure]]、[[architecture/material/MaterialBindless]]、[[architecture/cluster-streaming/Resident-Ranges]]。
