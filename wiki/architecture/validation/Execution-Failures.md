# Execution Failures

## Definition
设备丢失、native API 失败、I/O 失败、内存不足、不可用能力和真实损坏读取属于执行环境失败。

## Invariants
- shipping 必须正确传递或处理。
- 不和内部程序错误混成同一 validation 层。

## RenderGraph execution

`RenderGraphFrameState.Execute` 中传播的异常使图进入 Lost 状态。设备丢失按执行环境失败传递，其余执行期异常作为程序错误传播。后续 `TryBeginFrame` 抛出 `RenderGraphLostException`，由应用重建设备相关渲染对象。完整提交及执行前丢弃的状态规则见 [Frame Submission](../render-graph/Frame-Submission.md)。
