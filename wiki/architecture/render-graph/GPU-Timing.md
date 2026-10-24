# GPU Timing

本页记录公开的完成后计时接口；源码入口为
[`GpuTimes.cs`](../../../src/SomeEngine.RenderGraph/GpuTimes.cs)、
[`FrameRecording`](../../../src/SomeEngine.RenderGraph/FrameRecording.cs)、
[`FrameSlot`](../../../src/SomeEngine.RenderGraph/FrameSlot.cs)
和 [`ApplicationFrameTiming`](../../../src/SomeEngine.Runtime/ApplicationFrameTiming.cs)。

## 图入口

`RenderGraphFrameOptions.GpuTimes` 接收 `RenderGraphGpuTimes`，采集使用设备的
`CalibratedTimestamps` 能力。执行器在完成调度后，
为实际执行的每个 pass 记录四个 timestamp：前置屏障前、命令前、命令后、后置屏障后。
跨帧资源的队列释放也带有完整区间。记录覆盖持久 pass、当帧上传与其它即时 pass。

每个帧槽按实际 Queue 保存并复用 timestamp pool、readback buffer 和采样存储。
pool 的 QueueType 与写入和 resolve 所在队列一致。应用命令全部提交后，执行器在各自
队列上追加 resolve；同队列 FIFO 维持查询与读回顺序。resolve 完成点进入正常帧槽完成集合。
帧槽在完成后回收时交付结果，图正常释放时交付剩余已完成结果。

`GpuPassTime.CommandsMs` 表示 pass 命令的 GPU 时间。
`StartMs`、`EndMs` 表示含前后屏障的边界，均相对本帧首个 GPU 工作。
`ElapsedMs` 是所有实际队列的最早起点到最晚终点；每条队列通过
`CalibrateTimestamps` 映射到共享 CPU 时钟。阶段命令和可能重叠，整帧跨度直接由边界取得。
timestamp reset、resolve 和读回服务于计时；应用区间的首尾来自应用工作本身。

调用者通过 `PassOptions.TimeGroup` 或 `RenderGraphEdit.SetPassTimeGroup` 为阶段指定类别。
结果保留该帧执行时的类别、pass 身份、名称和实际队列，适用于之后的分组统计。
Cluster 通过 `DeformationTimeGroup` 标记容量重置与区域分配，
`DeformationEvaluationTimeGroup` 标记用户当前/上一顶点求值和缓存写入，
`RasterTimeGroup` 标记软件与硬件光栅工作。

采集期间 pass 逐帧录制，查询池与读回资源继续按帧槽复用。timestamp 命令、resolve
提交与完成后结果分配是采集的固定开销；比较配置应保持相同采集方式。

## 应用入口

`ApplicationScene.FrameMeasured` 将 `ApplicationFrameTiming` 送给调用者，保留正常异步
Game world 更新、异步 compute 与多帧调度。`Gpu` 是上述 GPU 结果，覆盖上传、姿势、
范围、剔除、变形、光栅、着色和最终输出的全部已执行图工作。

`CpuMs` 从帧入口与该帧异步 Game update 的调度起点中较早者计到 `Present` 返回，
因此包含该帧游戏回调、动画、提取、资源等待、渲染准备与提交。
`GameMs`、`AnimationMs`、`ExtractionMs`、`PrepareMs`、`ReconcileMs`、`ExecuteMs` 描述各 CPU 阶段；
`ExecuteMs` 包含图分析、命令录制和提交。CPU 与 GPU 结果通过 `FrameIdentity` 配对，
在 GPU 完成后交付给应用。

以下是应用接入例子，输出接收方由调用者提供：

```csharp
var scene = new ApplicationScene(contentDirectory)
{
    FrameMeasured = frame => receive(frame.CpuMs, frame.Gpu.ElapsedMs, frame.Gpu.Passes),
};
await Application.RunAsync(args, scene);
```

行为入口见
[`SkinCacheRenderingTests`](../../../tests/SomeEngine.Runtime.Tests/SkinCacheRenderingTests.cs)。

相关主题：[Frame Data](Frame-Data.md)、[Range Completions](Range-Completions.md)、
[Calibrated Timestamps](../RHI/Capabilities/Calibrated-timestamps.md)。
