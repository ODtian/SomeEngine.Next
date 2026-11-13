# Incremental Scheduling

RenderGraph 唯一持有一个 `GraphSchedule`。它保存持久 pass 的依赖与值来源、拓扑顺序、队列分配、队列前沿，以及每个 region 的 Start / End 资源状态和同步连接。

结构提交把 `GraphEditChanges` 交给这份共享调度。下一帧开始时，调度消费累计编辑并重建一次；影响调度的帧选项改变时采用同一路径。`GraphSchedule.Version` 在重建后递增。各 `FrameResources.ScheduleVersion` 记录自己已物化的版本，版本差异决定结构物化刷新。

资源版本分析按显式依赖投影到同一资源的使用者，随后按范围和纹理子资源连接读写关系。可禁用 pass 的值来源保留候选写入者，帧执行按实际启用状态选择来源。

`GraphSchedule` 统一分配持久 pass 和当帧段位置的行编号，并唯一持有当前连接投影 `FrameConnections`。各帧资源按同一编号物化资源、视图和录制数据。有效持久前驱、值来源和队列前沿由共享调度保存，连接投影直接引用这些数据。

连接输入相同时，投影保留已有依赖、执行序和前沿。当帧 pass、有效启用值、输入别名或范围改变时，只更新对应 consumer 与队列邻居，从变化点传播前沿和资源生命周期。持久队列位置之间预留当帧位置，段后的持久前沿按实际依赖恢复共享引用。`FrameRecording` 接收变化的 pass 编号，更新本帧物化、录制和提交缓存。

每个 region 在开始和结束处各有一个当帧段，公开参数为 `FrameSectionPosition.Start` / `End`。当帧 pass 在自己的段内分析依赖与局部顺序，再接到共享调度保存的进入、离开边界。跨队列连接由队列前沿表达；段末恢复所触碰持久资源的预期状态。

本帧按有效启用状态从持久访问中选择边界来源与队列前沿。帧输入解析出的资源别名与当帧访问一起连接到真实资源范围；这种当帧投影继续复用持久调度版本。

持久资源与外部注册资源的最终写入构成本帧可观察输出。持久 pass 与当帧段采用同一裁剪规则；通过 `frame.Import` 取得的交换链图像写入因此保留到提交。

晚绑定输入的值来源按实际绑定资源投影。图对该资源的使用者复用已有依赖与范围划分，先连接生产者的本帧值，再生成真实资源的读写同步。生产者等待其它资源时，输入消费方沿同一依赖链取得完成后的值。

本帧显式依赖和段边界先投影到执行顺序，再按该顺序连接真实资源的读写关系。新增依赖与已有位置冲突时，`PassOrder` 只对冲突区间排序，区间外的位置继续保留。最终位置保持一致的 pass 沿用已有录制版本。

`RenderGraphStatistics.PersistentScheduleReused` 表示共享调度复用，`FrameSectionPassCount` 表示本帧公开当帧 pass 数。内部段末同步 pass 归入同步实现。

参见 [[Retained-Graph]]、[[Incremental-Reconciliation]]、[[Incremental-Hazards]]。
