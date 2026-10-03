# Range Completions

## Definition
给定 Buffer 或持久 GraphBufferId 与 BufferRange，可以取得当前已经提交且仍可能访问该范围的 queue completions；每条 queue 至多保留一个需要等待的最大值。

## Invariants
- 不把不同 queue 的整数 completion 值互相比较。
- API 只报告已经提交的访问事实，不判断业务数据是否可以退休。
- 调用者必须先保证未来 frame 不再声明该范围。
- 查询不执行 CPU wait。

## Uses
Cluster page range 复用、Texture backing 回收、descriptor 回收和其它长期资源替换。

## Pass results during execution
`IRenderGraphFrameExecutionObserver.OnRenderGraphSubmitted` 在每个队列批次被接受后执行。
此时 `RenderGraphFrameResult.TryGetPassCompletion` 提供已经提交的 pass 完成点，
调用者通过后端完成查询确认 GPU 结果可读。后续批次继续录制及提交。

需要尽早消费结果的 pass 可设置 `PassOptions.SubmitAfter`，在该 pass 后结束队列批次。
持久图通过编辑接口或当帧 `SetPassOptions` 显式修改该边界，并增量维护受影响的批次。
`OnRenderGraphFrameFinished` 汇总当帧已经接受的 GPU 使用，供资源复用及回收等待使用。
图持久回读依据完整帧资源完成点交付缺页数据；几何页回收通过持久缓冲身份查询
各驻留范围的已提交使用。详见 [[architecture/rendering/Delayed-Feedback]]。

## Relations
[[architecture/RHI/Queue/Completion-value-domain|Completion Value Domain]] · [[architecture/cluster-streaming/Page-Eviction]]

## Behavior evidence

`FrameBufferRangesTests.PersistentBufferIdentityReportsOnlyItsUsedRanges` 通过 WARP、持久缓冲
身份与公开范围完成查询，验证写入范围获得实际提交完成点，其它范围保持空结果。
