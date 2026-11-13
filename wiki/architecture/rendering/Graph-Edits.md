# Rendering Graph Edits

保存图编辑结果的类型持有这次编辑。feature 在自身方法中依次执行 `BeginEdit`、创建或修改图、`Commit`；提交成功后，才把 pass 列表、缓冲 ID、区域 ID 和容量等结构数据写入成员。构建编辑所需的数据保存在局部变量中，提交异常沿原调用返回，已提交字段保留原值。

无状态的图注册辅助可以接收 `ref RenderGraphEdit`。它只返回由图 ID 组成的值，由调用方在提交成功后保存。`ScreenBinningPasses` 和共享扫描注册遵守这一分工，计算管线复用设备已有的 `RenderPipelines`。

RG 同时持有一个活动编辑。调用方先提交自己的资源创建，再把已提交的资源 ID 交给自持编辑的 feature。`LayerComposition.AddPass`、`RemovePass` 与扩容方法各自形成一次完整编辑；其公开接口接收 pass 描述和已提交资源。

帧提交、GPU 读回与图编辑各有明确时点。`OnFrameSubmitted` 采用成功提交后产生的本帧持久状态；`OnReadback` 只更新读回相关的测量值、来源帧 tag 和所需容量。`TryOpenNextFrame` 交付已完成的读回后，feature 自己的 prepare 系统在 `BeginFrame(next)` 之前读取该报告，自持一次编辑扩容，提交成功后采用新的容量。

在途帧数为 F 时，帧 N 的资源复用通常在 N+F 交付读回。当前 `CollectCompleted` 也收集其它已完成资源，反馈可以更早送达；扩容跟随实际交付的 prepare 时点。来源身份从读回 tag 传入 `Metrics.FrameIdentity`，检验按报告对应的帧与结构提交边界进行。

行为检查通过公共 API 观察活动编辑冲突、结构版本的推进、已提交 pass 列表和读回生效后的合成图像。规则对应 VSM 重构计划的 D14、ARCH-37、BEH-T1a、BEH-T1b 与 BEH-T1c。
