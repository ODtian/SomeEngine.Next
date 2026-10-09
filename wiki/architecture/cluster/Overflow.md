# Overflow

## Definition
Overflow 是 fixed-capacity work 被拒绝，不是视觉正确性恢复信号。

## Invariants
- 必需的 hierarchy/candidate/deferred/visible-window/material-batch/surface-patch 工作被拒绝时，
  该帧不是完整渲染结果。饱和计数和写入 guard 保护内存，不表示质量恢复。
- 同一个全帧 GPU status 持有各阶段失败；VSM depth completion 读取它，完整深度才进入缓存。
  页请求读回记录携带该 status 的 4 字节值，CPU 在已有 GPU 完成点消费并抛出
  `ClusterWorkOverflowException`。应用归类为 `Rendering`，在交付图片或报告成功前收集完成反馈。
- 当前策略是可区分功能失败，不通过粗化几何、降低分辨率、扩大缓冲或 CPU 保守复制链
  伪装完成；没有 same-frame host decision → GPU rerun。后续 split/retry 必须证明完整工作消费。
- 公共 `CaptureFrameMetrics().Work` 区分候选测试、HW 潜在/空/必需/接受窗口、SW 必需/接受项、
  材质 batches 与 surface patches。诊断沿同一 GPU holder 回读，测试不冻结队列/储存结构。
- `Work.Complete` 只表示 geometry work 未被拒绝，不代替页分配成功、HiZ 历史有效或全部页可采样。
- page-fault queue overflow 可以丢 fault；未来 traversal 再遇到 nonresident page 会重新 fault。

## Relations
[[Worklists]] · [[architecture/rendering/Delayed-Feedback]]
