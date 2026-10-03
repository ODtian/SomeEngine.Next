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

## Cluster consumption

Cluster 在反馈区段安装页缺失和可选帧指标持久回读。录制时设置的标签是该帧使用的
驻留 epoch；页缺失接收者保存有效队列字节，`ClusterStreamingFeedbackConsumer.Pump`
在之后的 prepare 阶段将数据交给流送。`CaptureStatistics` 提供已交付/已消费帧数、
页缺失数量和最近 epoch，供行为验收观察。

页缺失接收者为完成反馈附加递增的消费序号，流送按 epoch 与序号验证批次。
帧指标缓冲同时保存录制帧序号，`ClusterFrameMetrics` 在完成交付后提供指标与实际 epoch 标签。

## Relations

[[architecture/cluster/Overflow]] · [[architecture/cluster-streaming/Page-Faults]] ·
[[architecture/render-graph/Readback]] · [[architecture/render-graph/Frame-Uploads]]
