# Renderer pipeline requests

`ApplicationRenderer` 为自己的 Device 创建并持有一个 `RenderPipelines`。它把同一实例传给
`RenderDevice`、Cluster 管线集及各 feature。`CreateCompute`、`CreateGraphics`、`CreateMesh`
返回共享的 `Task<Pipeline>`；每次底层异步创建都传入该服务持有的 `PipelineCache`。

服务保存 shader 代码身份、编译字节和完整管线状态。提供代码 blob 时，比较 blob 中的实际字节；其余请求比较描述中的代码序列。相同描述复用同一任务；标签用于诊断。
材质、feature 和场景借用创建结果。管线保留到渲染器退出，因此返回已访问场景时可以复用
此前创建的 PSO。Cluster 的材质 raster 管线按具体材质 entry 创建，generic shader 模板作为
组合输入保留。

缓存路径为 `Library/PipelineCache/{backend}-{vendorId:x4}-{deviceId:x4}-{driverVersion}.bin`。
启动时读取已有 envelope，RHI 按版本与环境选择匹配条目；无效 envelope 使用空缓存重新开始。
渲染器退出时先结束消费者并释放图中的可复用命令，再由 `RenderPipelines.Dispose` 等待创建任务结束、写回缓存并释放
所持管线与缓存。

`RenderPipelinesTests` 通过公共入口验证相同描述共享任务，以及保存后重新打开设备缓存的
创建请求命中 `D3D12Diagnostics.PipelineCreation` 所报告的缓存。
