# Delayed Feedback

## Definition
设备到 host 的 streaming、统计、overflow 与 diagnostics 信息可以延迟读取，但不得形成同一 frame 的设备→host→设备依赖环。

## Invariants
- Frame N 的 GPU feedback 只影响 N+k。
- readback 不触发同帧重跑。
- page fault、high-water 和统计都遵循此规则。

## Delivery boundary

RenderGraph 的持久回读在帧槽位的全部队列完成点就绪后，由 `CollectCompleted` 交付字节和
录制时的标签。`TryOpenNextFrame` 在选定下一帧资源前调用这个收集入口，并在等待选定
帧槽位完成后收集其结果。因此，Frame N 的结果供之后的帧准备阶段消费。

`PassOptions.SubmitAfter` 可在回读复制后结束队列批次，交付继续以完整帧槽位的完成边界为准。
具体声明、启用、取消和单次交付规则由 [[architecture/render-graph/Readback]] 持有。

## Relations

[[architecture/cluster/Overflow]] · [[architecture/cluster-streaming/Page-Faults]] ·
[[architecture/render-graph/Readback]] · [[architecture/render-graph/Frame-Uploads]]
