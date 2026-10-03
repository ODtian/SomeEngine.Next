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
- 区段启用状态与 [[Extension-Points|Frame Regions]] 的当帧内容

## Invariants
更新这些数据不得改变 graph identity，也不得触发结构重建。

## Upload bytes

当帧 `Upload` 共用图管理的映射页。生成或重排上传数据时，可提供字节数、数据及
写入回调，直接填充最终上传区。回调在 `Upload` 返回前完成，写入声明的全部字节，
图清零对齐填充并将成功写入的范围注册到当帧。上传页按帧完成点复用，取消帧和
写入异常沿原帧资源回收路径处理。

声明上传缓冲通过 `SetBufferData` 或 `GetBufferDataWriteSpan` 填充图持有的当帧映射空间，
保留其图身份与绑定范围。具体存储和完成边界见 [[Frame-Uploads]]。

## Persistent buffer uploads and readback tags

`RenderGraphNextFrame.GetWriteSpan` / `WriteBuffer` 和 `RenderGraphFrame` 中的同名方法按目标
持久缓冲及偏移登记变化字节。复制范围属于本帧数据，帧上传的开帧、分配和取消规则由
[[Frame-Uploads]] 持有。

`RenderGraphFrame.SetReadbackTag` 设置录制标签，`SetReadbackEnabled` 设置回读启用状态。
启用状态与所在区段共同控制执行，交付记录保存录制时的标签。回读声明、内存和交付边界
由 [[Readback]] 持有。

## Buffer ranges

`RenderGraphFrame.SetBufferRanges(GraphBufferId, BufferRangeSet)` 为缓冲设置当帧可读范围集合。
一个集合覆盖当帧所有使用该缓冲的读取 descriptor，包括持久 pass 和当帧 pass；图沿普通
范围访问处理依赖、同步和完成点。集合属于当帧，图身份和 descriptor 的原有范围保留。

`BufferRangeSet` 持有不可变范围。范围相同时可逐帧传入同一集合；变化时创建新的集合。
`BufferRangeSet.Empty` 表达该帧的空读取集合。`ReadOnlyBufferDescriptor` 从当帧集合或
pass 单独声明的普通范围读取获得实际使用范围。直接声明的复制访问继续使用各自声明的范围。

Cluster 对页堆每帧调用一次 `SetBufferRanges`，所有页堆绑定共用驻留范围。

## Relations

[[Retained-Graph]] · [[Structural-Changes]] · [[Resource-Access]] · [[Frame-Uploads]] · [[Readback]]
