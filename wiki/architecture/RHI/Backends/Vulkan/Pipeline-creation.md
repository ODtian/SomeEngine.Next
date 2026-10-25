# Vulkan 异步管线创建

公开 `IGraphicsBackend.CreateGraphicsPipelineAsync`、`CreateComputePipelineAsync` 及高级管线创建入口返回原有 `Task<Pipeline>`。调用时物化描述数据，保留 Slang 数据、代码与 PipelineCache 使用引用，并捕获调用处的 `JobSystem.Current`。

设备的 256 容量 Channel 继续持有待派发请求，容量等待使用原 `WriteAsync` 行为。设备按处理器数量保留一到四个派发额度；已派发且等待共同执行的请求保留在设备节点链。各 Scheduler 的 `Vulkan pipeline` 类别使用 256 在途额度、至多四个且受 worker 数量限制的执行额度，服务份额为 1。`ScheduleAsync` 获准后由具体 `IJob` 执行原生创建，完整 Job 结束后归还设备派发额度。

原请求持有唯一 Pipeline Task。实际执行先移除等待节点并登记 running，创建管线、归还输入和缓存使用引用、交付原 Task，然后恢复此前的线程缓存上下文并归还 running。原缓存 Gate 继续保护 Vulkan 对缓存的访问。

设备接收状态依次为 Accepting、Ending、Ended。停止接收完成 Channel，并在队列锁外归还待派发及已派发等待请求的数据，向原 Task 交付关闭或 Device Lost 结果。设备关闭等待 Ended、尚未完成的 Channel 写入和 running 请求归零，因此包含输入清理及 Task 终态交付。

实现入口为 [VulkanPipelineWorker](../../../../../src/SomeEngine.Graphics.Vulkan/VulkanPipelineWorker.cs) 和 [VulkanPipelineCache](../../../../../src/SomeEngine.Graphics.Vulkan/VulkanPipelineCache.cs)。公开检查 `VulkanFaultAndLifetimeTests` 覆盖缓存使用期与排队请求关闭，`VulkanPipelineTests` 通过实际 GPU 输出消费异步创建的管线，`VulkanFrameLoopPresentsItsCapturedFrame` 沿公共应用入口完成呈现与图像检查。
