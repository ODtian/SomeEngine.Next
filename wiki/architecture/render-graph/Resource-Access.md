# Resource Access

## Definition
RenderGraph 的同步与生命周期事实来自 pass 对真实 Graphics 资源及其范围的 Read、Write、ReadWrite 声明。

## Invariants
- descriptor 数值不是依赖关系。
- parameter binding 可以同时完成绑定和 graph access 声明。
- persistent pass 可逐 frame 增加普通 resource access。
- 不建立单独的 bindless access-set 数据通路。

共享 UAV 的写入通过 `UnorderedAccessHazardMode` 表达执行关系：
`AtomicMergeable` 表示各写入使用原子操作合并，`DisjointWrites` 表示各写入覆盖
互不交叠的数据。相同队列中连续的这类写入共用访问状态，依赖消费者读取时统一
建立可见性屏障。Cluster 的可见性 winner、工作统计和阴影页请求使用原子合并；
材质 bin 与背景填充写入各自覆盖的像素。

跨队列同步同时遵循后端资源访问规则。写入同一输出的材质 bin 聚集在同一队列，
各 bin 通过共享提交和连续 UAV 状态获得 GPU 重叠执行机会。写入独立资源的分支
由兼容队列调度分配。

## Failure

当 Buffer、Texture、Query 或 RayTracingShaderTable 的读取范围包含未定义内容时，帧执行抛出公共 `UndefinedResourceReadException`。调用方通过异常类型区分资源内容失败，诊断消息提供 pass 与资源上下文。

## Relations
[[Shader-Resource-Closure]] · [[architecture/material/MaterialBindless]] · [[architecture/cluster-streaming/Resident-Ranges]]
