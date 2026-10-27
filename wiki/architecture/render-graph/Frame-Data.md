# Frame Data

## Definition
Frame data 是在既有图结构中变化的值和绑定。

## Includes
- camera / view 数据
- constants
- external resource 的实际绑定
- dynamic buffer/texture ranges
- material values
- instance values
- frame-local resource accesses

## Invariants
更新这些数据不得改变 graph identity，也不得触发结构重建。

录制批次按 pass 的参数声明汇总描述符容量。容量随批次保存，参数声明刷新时重新汇总；
帧数据与资源绑定变化沿现有命令失效路径刷新录制内容。
当前职责可由 [FrameRecording](../../../src/SomeEngine.RenderGraph/FrameRecording.cs)
中的 `ConnectParameterBindings` 与 `RecordBatch` 核对。

## Upload bytes

当帧 `Upload` 共用图管理的映射页。生成或重排上传数据时，可提供字节数、数据及
写入回调，直接填充最终上传区。回调在 `Upload` 返回前完成，写入声明的全部字节，
图清零对齐填充并将成功写入的范围注册到当帧。上传页按帧完成点复用，取消帧和
写入异常沿原帧资源回收路径处理。

## Relations
[[Retained-Graph]] · [[Structural-Changes]] · [[Resource-Access]]
