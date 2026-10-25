# GPU Timing

本页记录公开的完成后计时接口；源码入口为
[`GpuTimes.cs`](../../../src/SomeEngine.RenderGraph/GpuTimes.cs)、
[`FrameRecording`](../../../src/SomeEngine.RenderGraph/FrameRecording.cs)、
[`FrameSlot`](../../../src/SomeEngine.RenderGraph/FrameSlot.cs)
和 [`ApplicationFrameTiming`](../../../src/SomeEngine.Runtime/ApplicationFrameTiming.cs)。

## 图入口

`RenderGraphFrameOptions.GpuTimes` 接收 `RenderGraphGpuTimes`，采集使用设备的
`CalibratedTimestamps` 能力。调用者在设备创建请求中指定
`DeviceFeatures.CalibratedTimestamps`；普通应用已将它列入可选特性。
执行器在完成调度后，
为实际执行的每个 pass 记录四个 timestamp：前置屏障前、命令前、命令后、后置屏障后。
跨帧资源的队列释放也带有完整区间。记录覆盖持久 pass、当帧上传与其它即时 pass。

每个帧槽按实际 Queue 保存并复用 timestamp pool、readback buffer 和采样存储。
pool 的 QueueType 与写入和 resolve 所在队列一致。执行器沿已解析的 wave 提交顺序，
为每条实际队列保存最后一次应用提交，并在其命令数组尾追加独立短 context 记录的 resolve。
只有跨帧资源释放工作的队列，将 resolve 追加到该次释放提交。查询写入与 resolve 按同队列
命令顺序执行，共用该次提交的完成点；正常资源状态接受、跨队列等待和帧槽完成集合沿用该完成点。
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

采集期间持久 pass 按已有资源、参数与屏障条件复用命令，并同时比较该 pass 实际的
Queue、timestamp Pool 对象与查询起点。池增长或查询绑定改变时，受影响的录制批次重新记录。
查询 reset 与四个 timestamp 随命令一起重放，查询池与读回资源继续按帧槽复用。
resolve 的短命令每帧重新记录，应用的长批命令沿原重放条件复用。
timestamp 命令、resolve 录制与完成后结果分配是采集的固定开销；比较配置保持相同采集方式。

图提交、完成后多队列时间和回读的公开行为见
[`QueueSubmissionTests`](../../../tests/SomeEngine.RenderGraph.Tests/QueueSubmissionTests.cs)。

## 应用入口

`ApplicationScene.FrameMeasured` 将 `ApplicationFrameTiming` 送给调用者，保留正常异步
Game world 更新、异步 compute 与多帧调度。`Gpu` 是上述 GPU 结果，覆盖上传、姿势、
范围、剔除、变形、光栅、着色和最终输出的全部已执行图工作。

`CpuMs` 从帧入口与该帧异步 Game update 的调度起点中较早者计到 `Present` 返回，
因此包含该帧游戏回调、动画、提取、资源等待、渲染准备与提交。
应用等待短 Game 任务时，`RuntimeWait` 先在同一超时预算内主动检查至多 1 ms，
随后按原完成事件与窗口消息等待；该段等待完整计入 `CpuMs` 与 `TaskCompletion` trace。
`GameMs`、`AnimationMs`、`ExtractionMs`、`PrepareMs`、`ReconcileMs`、`ExecuteMs` 描述各 CPU 阶段；
`ExecuteMs` 包含图分析、命令录制和提交。CPU、GPU 与 `Geometry` 结果通过 `FrameIdentity` 配对，
在 GPU 完成后交付给应用。`RenderGraphFrameResult.FrameIdentity` 将执行观察与同一帧关联。
`SimulationSeconds` 保留该帧 Game world 实际累计的步长，可用于对齐两种配置的源时钟。
`AsynchronousGameUpdates` 记录应用实际选择的 Game 调度方式。
`ApplicationScene.FrameCompleted` 在应用线程接收呈现后的帧，并先于下一次异步 Game update
调度执行。平台通过该边界消费渲染反馈；实际 Game 编辑使用 `UpdateFrame`，沿当前帧的同步编辑路径执行。
`ApplicationScene.CaptureImage` 将截图请求移交给下一帧的普通图像读回。
`Geometry` 包含两个主视图 occlusion 阶段选中 cluster 的顶点项和三角形项，以及用户变形
区域的实际分配字节；顶点项包含 cluster 和实例之间的重复。
这些 counters 通过完成后的普通异步读回交付，`ClusterPipelineSystem.FrameMeasured` 是
直接使用 Cluster 时的完成通知。

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
统一平台的完整分布和实际工作量用例见 [Crowd Example](../animation/Crowd-Example.md)。

相关主题：[Frame Data](Frame-Data.md)、[Range Completions](Range-Completions.md)、
[Calibrated Timestamps](../RHI/Capabilities/Calibrated-timestamps.md)。
