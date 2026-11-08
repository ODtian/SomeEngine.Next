# Resource Retirement

## Definition
被图替换或删除的 Graphics 资源在所有已经提交且仍可能访问它的工作结束后才能释放。

## Invariants
- 释放依据真实 queue completion，而不是 frame 编号猜测。
- 资源替换不要求 CPU 等待 GPU 空闲。
- future frame 已经切换到新资源后，旧资源才能进入等待释放集合。

[`RenderGraph.TryOpenNextFrame`](../../../src/SomeEngine.RenderGraph/RenderGraphNextFrame.cs)
在选中帧存储的全部 queue completion 就绪后，先调用 backend 的 `CollectCompleted`，
再重置帧数据。完成的提交由此释放原命令录制的使用权，后续帧可沿同一录制上下文复用命令。
完成观察与退休收集的职责见 [Completion value domain](../RHI/Queue/Completion-value-domain.md)。

## Relations
[[Graph-Versioning]] · [[Range-Completions]]
