# Frame CPU Overlap

## Definition
GameWorld 下一轮 CPU 工作可以与已经交付给渲染侧的上一轮 CPU/GPU 工作重叠。

## Invariants
- render prepare 完成后 renderer 不再依赖该轮 GameWorld 的活跃访问。
- Game N+1 不需要等待 Render N GPU 完成。
- 需要设备反馈的决策只能作用到未来 frame。

GPU 完成由 RenderGraph 集中跟踪。`LastSubmittedFrame` 表示最近完整提交的帧，`LastCompletedFrame` 按提交顺序推进；`IsFrameComplete` 查询指定帧，`WaitForFrame` 等待该帧关联的队列值。渲染功能通过帧号表达数据使用期，资源退役通过 `RetireAfterSubmittedFrames` 交回图处理。


每台设备的 `ApplicationRenderer` 持有 RenderWorld、提取与准备系统、RenderGraph、RenderDevice、ShaderLibrary、RenderPipelines 和各渲染功能。场景加载保留这些对象。`ApplicationScene.LoadScene(directory)` 启动后台内容任务，当前场景继续执行帧循环；新内容完成后切换 GameWorld，再从新的来源执行完整提取。旧 `ApplicationSceneContent` 经 `RetireAfterSubmittedFrames` 释放。`ApplicationFrame.Index` 使用 renderer 的累计帧号。

准备系统按注册顺序运行，随后实例数据将 CPU 变化区间写入 RG 持久缓冲。帧只通过 `Execute` 提交一次，等待通过 `WaitForFrame` 进行。游戏更新前先应用完成帧的 GPU 反馈；`nextGameUpdate` 表达游戏 CPU 工作与渲染线程之间的重叠。

`RenderInstanceData` 持有实例属性和元数据各一份托管字节数组，`ReadInstance<T>` 直接读取这些数组。写入收集变化范围，准备结束时合并范围；帧构建把对应字节写入 `PropertyData`、`Metadata` 两个 RG 持久缓冲。`OnFrameSubmitted` 清除已提交范围，取消帧保留这些上传义务。

## Relations
[[Extraction]] · [[Delayed-Feedback]]
