# Asynchronous GPU Pose Readback

本页记录已实现的选骨读回、执行身份及共同叶节点反馈。源码入口为 [请求与结果](../../../src/SomeEngine.Animation.Pose/GpuPoseReadback.cs)、[提取](../../../src/SomeEngine.Render.Features/Skinning/MeshSkin.cs)、[GPU copy 与完成](../../../src/SomeEngine.Render.Features/Skinning/GpuPoseReadback.cs) 和 [共同叶节点完成](../../../src/SomeEngine.Animation/AnimationGraph.cs)。

## 请求与结果

`GpuPoseAnimation.RequestReadback(world, target, range)` 为该 GPU target 当前已经形成的来源输入请求自身骨骼。`PoseRange.Start` 与 `Count` 使用该 Skeleton 的自身索引；继承骨骼按其实际生产 target 请求。调用位于共同 CompositionInputs 形成输入之后、正常提取之前，例如具体算法的 Composition 系统。

返回的 `GpuPoseReadbackRequest` 保留 `Identity`、`Range` 和 `Completion`。`GpuPoseIdentity` 包含 Game target 实体、`SkeletonInstance` 引用、`Starts`、`AnimationStarts` 与 `InputNumber`。请求经 `GpuPoseTarget.Readbacks` 的只读集合进入同次正常提取。显式请求时替换集合并保留尚在执行的请求。

Rendering world 从共享骨骼缓冲中复制选定 current、previous 范围，以及对应 GPU 播放行。copy 排在 `SkinningFeature.CompletePass` 之后；提交观察者记录该 copy 实际接受的 queue completion。`CompleteReadbacks()` 查询完成点，就绪后映射、invalidate 并复制为独立的 CPU 数据，然后完成请求 Task。`GpuPoseReadbackRequest.Deliver` 是该提取后 GPU 生产方的交付入口。

| 结果字段 | 含义 |
| --- | --- |
| `Identity`、`Starts`、`InputNumber` | 产生结果的实例和实际 GPU 来源身份 |
| `Range` | 请求的自身骨骼范围，返回数组从该范围的第一根骨骼开始 |
| `Current`、`Previous` | 当前求值及上一份实际求值的模型空间 G |
| `Version`、`CalculationTime` | 完整显示姿势的求值版本与时钟，包含继承父结果依赖 |
| `SourceCalculationTime`、`SampleTime`、`Playback` | 实际 GPU 来源输入时钟、样本时间与状态/过渡信息 |
| `Calculations` | 范围内每根骨骼的版本、计算时钟与局部来源时钟，CPU 写入项保留生产方信息 |

结果持有只读内存，使用期独立于读回缓冲、后续播放与图形设备。Rendering world 释放完成的读回缓冲；feature 释放时排空已经提交的复制。播放重新开始后，先前请求继续持有其原执行身份，新请求记录新的启动标记。

copy 所在帧在提交前取消时，选骨请求的 `Completion` 转为取消，并释放对应读回缓冲。消费者在下次 Game 更新为当前输入重新请求。feature 关闭时同样取消尚未提交的请求；已提交的 copy 按其完成点交付。

## 在共同图中消费反馈

具体叶节点照常通过 `IAnimationLeaf<TParameters>` 安装自己的逐实例请求与算法历史，系统使用 `AnimationGraph.ReadTime` 取得当前叶的实际启动标记。`GpuPoseTarget.Starts` 保留这一实际标记：顶层叶采用 Timeline 身份，嵌套叶采用对应 GraphMotion 身份。`AnimationStarts` 保存安装时 `AnimationLeafPlacement.Starts`，区分整图的各次播放。嵌套 motion 在新图播放时可以从局部 Starts 1 开始，两层标记共同识别该执行。内置 GPU 叶在安装时写入 `AnimationStarts`；自定义生产叶采用相同字段。

Rendering world 把安装标记变化纳入来源身份，并重置对应 GPU 播放历史；实际 `Starts` 继续表达该叶的状态进入或局部重新开始。

以下是消费者算法系统中的调用示例，属于公共扩展用法。`request` 由该叶实例持有，`Converged` 表达调用方算法对完成结果的判断。

```csharp
if (request is null || request.Completion.IsCanceled)
    request = GpuPoseAnimation.RequestReadback(world, target, requiredBones);
bool finished = request.Completion.IsCompletedSuccessfully
    && Converged(request.Completion.Result);
bool accepted = GpuPoseAnimation.SubmitFeedback(
    world, animation, target, placement, request, finished);
```

`SubmitFeedback` 比对结果与请求的完整身份和范围，并比对当前 target、实例、叶的实际 `Starts` 及安装 `AnimationStarts`。异步结果匹配其原请求的输入编号；当前 Game 输入可以继续推进。它通过共同 `AnimationGraph.Submit` 提交 running，`StageEnd.Settled` 在匹配结果满足算法完成条件后进入后续阶段。该系统位于 Composition，因此共同 GraphEndSystem 与 TimelineEndSystem 消费同次提交。

时长节点继续采用 `MotionBoundary(StageEnd.Duration, duration)` 和共同时间语义。收敛算法在自己的结果判据满足时提交完成；需要下一次求解时按当前输入发起新请求，继续持有自己的历史。

Game 侧在正常更新窗口读取 Task 的完成值并写入 ECS。Renderer 的交付只完成普通请求对象，Game 参数及运行状态由 Game 系统更新。

## 应用诊断入口

`SceneEdit.ReadGpuPose(target, receive)` 与 `SkinningFeature.RequestReadback(section, receive)` 请求完整自身区段，复用相同范围复制与完成实现。应用在帧提交后轮询，GPU 已完成时交付 callback；返回数据采用相同身份、范围和计算信息。

诊断 callback 沿下一次实际提交交付；取消帧后重录时，结果的身份、计算时间和版本来自该次提交。

公共应用示例见 [GpuPoseFeedbackTests](../../../tests/SomeEngine.Runtime.Tests/GpuPoseFeedbackTests.cs)。同步关键骨骼的当次 Game 完成点见 [CPU/GPU Pose Handoff](CPU-GPU-Pose-Handoff.md)。
