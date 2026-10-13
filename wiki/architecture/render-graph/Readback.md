# Readback

## Definition

持久回读把缓冲中的固定范围交给 CPU 接收者。`RenderGraphEdit.AddReadback(source, range,
receiver, label, region, options)` 安装持久复制 pass，并返回 `GraphReadbackId`。
源缓冲提供 `CopySource` 用途；`BufferRange.Whole` 在安装时按缓冲大小解析。

接收者实现 `IGraphReadbackReceiver.OnReadback(ReadOnlySpan<byte> bytes, ulong tag)`。
字节跨度在这次回调期间有效，接收者可以在回调内消费或复制数据。

## Frame data

`RenderGraphFrame.SetReadbackTag` 设置 `ulong` 标签，默认值为零。执行取得录制时的标签，
交付记录保存该值。随后帧更新标签时，已经提交的结果继续携带各自录制时的值。

`SetReadbackEnabled` 控制复制 pass 的启用状态，所在区段的启用状态共同控制执行。
`RenderGraphEdit.Remove(GraphReadbackId)` 沿 pass 编辑路径移除复制及其访问。

结构索引随 pass 的显式编辑维护回读 pass 列表，帧准备直接遍历该列表，为启用的回读
分配目标范围。列表按图中的 pass 顺序保存，并随回读安装、删除和替换更新。

## Completion and memory

目标内存来自对应帧槽位的回读页，同一帧的回读共享页内空间。复制使用普通源范围读取和
目标范围写入声明，参与 RenderGraph 的同步、调度与资源完成跟踪。

回读页保留已接受提交后的实际缓冲边界状态，下一次使用该页时继续作为图的入口状态。
队列变化时，图据此生成释放、取得与等待；这些状态与页内存一起由帧资源持有。

成功执行的帧保存字节范围、接收者和标签。帧槽位的全部队列完成点就绪后，
`RenderGraph.CollectCompleted` 映射并交付结果，然后回收该帧的资源。每帧每个回读交付一次，
重复收集保留这一行为。取消帧丢弃交付记录，禁用状态使对应回读留在图中等待重新启用。

收集先取走本次交付记录，再依次调用接收者。接收者抛出异常时，其余结果继续交付，
本次收集以聚合异常传播错误。页的复用时机仍由帧槽位的完成点决定。

安装时可通过 `options` 设置 `PassOptions.SubmitAfter`，在回读 pass 后结束队列批次。
回读交付继续采用完整帧槽位的完成边界。下一帧打开时的收集入口及反馈时机见
[[architecture/rendering/Delayed-Feedback]]。

## Behavior evidence

`tests/SomeEngine.RenderGraph.Tests/PersistentReadbackTests.cs` 使用 WARP、自给的 16 字节
缓冲和 RenderGraph 公共 API，覆盖完成后单次交付、录制标签、启用恢复、帧取消与提早提交。
队列门控通过 RHI 的外部时间线构造真实在途工作，结果通过持久回读接收者观察。

## Relations

[[Frame-Data]] · [[Frame-Uploads]] · [[Regions]] · [[Range-Completions]] ·
[[architecture/rendering/Delayed-Feedback]]
