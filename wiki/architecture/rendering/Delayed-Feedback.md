# Delayed Feedback

## Definition
设备到 host 的 streaming、统计、overflow 与 diagnostics 信息可以延迟读取，但不得形成同一 frame 的设备→host→设备依赖环。

## Invariants
- Frame N 的 GPU feedback 只影响 N+k。
- readback 不触发同帧重跑。
- page fault、high-water 和统计都遵循此规则。

## Relations
[[architecture/cluster/Overflow]] · [[architecture/cluster-streaming/Page-Faults]]
## 姿势结果

`GpuPoseReadbackRequest` 是身份与骨骼范围的值。渲染侧 RG 读回完成后，把每骨骼的 `GpuPoseReadback` 纯值连同产生帧号写入 `SkinningFeature.PoseFeedback`。矩阵、计算时钟、源采样时间和播放状态沿同一条 `RenderFeedback<T>` 队列交付。

GameWorld 更新前，`GpuPoseFeedbackSystem.Apply(world, graph.LastCompletedFrame)` 只提取已完成帧记录，按请求身份与骨骼范围组合为 `GpuPoseReadbackResult`，写入 `GpuPoseTarget.CompletedReadbacks`。算法通过 `GpuPoseAnimation.TryGetReadback` 或 `SubmitFeedback` 消费 GameWorld 中的结果。

场景切换时，新 GameWorld 的反馈系统以切换前的 `LastSubmittedFrame` 为下界，从此之后产生的帧记录才参与该世界的请求匹配。旧世界的在途 GPU 数据仍通过同一队列完成回收。
