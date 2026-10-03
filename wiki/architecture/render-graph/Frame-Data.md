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

## Upload bytes

当帧 `Upload` 共用图管理的映射页。生成或重排上传数据时，可提供字节数、数据及
写入回调，直接填充最终上传区。回调在 `Upload` 返回前完成，写入声明的全部字节，
图清零对齐填充并将成功写入的范围注册到当帧。上传页按帧完成点复用，取消帧和
写入异常沿原帧资源回收路径处理。

## Persistent buffer uploads and readback tags

`RenderGraphNextFrame.GetWriteSpan` / `WriteBuffer` 和 `RenderGraphFrame` 中的同名方法按目标
持久缓冲及偏移登记变化字节。复制范围属于本帧数据，帧上传的开帧、分配和取消规则由
[[Frame-Uploads]] 持有。

`RenderGraphFrame.SetReadbackTag` 设置录制标签，`SetReadbackEnabled` 设置回读启用状态。
启用状态与所在区段共同控制执行，交付记录保存录制时的标签。回读声明、内存和交付边界
由 [[Readback]] 持有。

## Relations

[[Retained-Graph]] · [[Structural-Changes]] · [[Resource-Access]] · [[Frame-Uploads]] · [[Readback]]
