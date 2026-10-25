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

## Parameter block updates

每个参数块持有自己的连接、已解析资源值和绑定缓存。帧执行先完成资源实例化及参数输入更新，
再将变更连接与待更新缓存按参数块合并，通过当前调度器的 `Scheduler.ExecuteParallel` 更新各块。
各任务读取本帧已有资源与输入，只写入对应块的数据和缓存。

同步调用返回后，帧执行线程合并命令失效标记、下帧待更新块及旧绑定释放，再进入同步解析和命令记录。
图与帧的既有资源集合保持输入资源有效；已记录命令沿 RHI 完成点保持其引用的绑定版本与 GPU 资源有效。

实现入口：[FrameExecutor.RefreshParameterBindings](../../../src/SomeEngine.RenderGraph/FrameRecording.cs)。
公开行为检查：[GraphParameterUpdateTests](../../../tests/SomeEngine.Graphics.Direct3D12.Tests/GraphParameterUpdateTests.cs)
通过图参数更新、帧执行与像素读回核对跨帧普通数据及资源输入。

## Relations
[[Retained-Graph]] · [[Structural-Changes]] · [[Resource-Access]]
