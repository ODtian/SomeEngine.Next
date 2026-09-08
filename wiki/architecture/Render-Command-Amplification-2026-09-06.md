# 渲染命令数量放大审计

本页保留优化前的审计记录。后续实现、验证与前后测量见 [渲染命令优化交付](Render-Command-Optimization-2026-09-06.md)。

本次只审计现有实现，没有修改产品调度或删除命令。使用自有 C# trace 解析器重新读取 `motion-async-full-1.setrace` 与 `motion-graphics-full-1.setrace` 的第 257–512 帧，通过原生区间的执行祖先区分普通录制、普通提交和 late release。所有线程合计；与上一份报告的主线程计数不同。

| 层级 | 开启异步计算 | 关闭异步计算 |
|---|---:|---:|
| 执行 pass | 107 | 107 |
| 波次 | 25 | 13 |
| 普通提交批次 | 28 | 17 |
| 普通命令列表 | 71 | 48 |
| 仅一个 pass 的普通列表 | 51 | 28 |
| 仅 late release 的列表及提交 | 13 | 7 |
| 原生列表总数 | 84 | 55 |
| ExecuteCommandLists | 41 | 24 |
| Queue.Signal | 41 | 24 |
| Queue.Wait | 38 | 24 |

开启异步计算的普通列表分布：51×1 pass、15×2、2×3、1×5、1×7、1×8，共 71 张、107 个 pass。84 张列表中主线程录制 80 张，工作线程录制 4 张。关闭异步计算的主线程列表数为 48，全线程数为 55。

## 合并止于哪里

[BuildWaves](../../src/SomeEngine.RenderGraph/FrameRecording.cs:115) 按跨队列依赖增加波次。[BuildBatches](../../src/SomeEngine.RenderGraph/FrameRecording.cs:859) 只在一个波次内按队列聚合，[RecordAndSubmit](../../src/SomeEngine.RenderGraph/FrameRecording.cs:59) 每波录制并提交，没有继续沿各队列寻找可以跨波次合并的提交区间。跨队列依赖当然必须保留；当前波次划分却也直接决定了提交粒度，尚未证明每个边界都是最少必要边界。

[RecordBatch](../../src/SomeEngine.RenderGraph/FrameRecording.cs:308) 又在每个带 acquire 的后续 pass 前无条件切开列表。切出的列表最终仍由同一个 SubmitBatch 放进一次 ExecuteCommandLists，所有队列等待在整批列表之前发出，不会因为切列表就插入新的队列等待。

D3D12 enhanced barrier 的 SYNC_NONE/NO_ACCESS 限制以 ExecuteCommandLists 范围为准，不以单张列表为准。因此这种切列表不能单独建立新的 barrier 执行范围。合并时仍须保留合法 barrier 和命令顺序，以及真正的并行录制分工；71−28=43 张额外普通列表包含 acquire 分段和并行分工，不能直接宣布 43 张全部冗余。[微软规范](https://microsoft.github.io/DirectX-Specs/d3d/D3D12EnhancedBarriers.html#synchronization)

[SubmitLateReleases](../../src/SomeEngine.RenderGraph/FrameRecording.cs:787) 根据消费者所在波次筛选跨帧资源的 release，再按源队列分组，每组新开列表、录 barrier、单独提交。测量中的 13 次额外提交正来自这里，约占总提交次数 32%。它们没有 pass 回调，但包含真实 release barrier；不能当作空列表直接删除。可以研究把必要的 release 放入合适的已有源队列提交，或按真实边界状态消除不必要的 release，不能根据预测下一帧输入提前改变资源状态。

特别是 [ResolveInitialBufferTransfers](../../src/SomeEngine.RenderGraph/FrameSynchronization.cs:810) 在源和目标队列类型不同时生成 buffer release/acquire；D3D12 后端再将其翻译成 barrier。这条跨 API 表达需要按 D3D12 的 buffer 无布局、ExecuteCommandLists 边界和真实 fence 依赖判断是否需要实际 barrier。当前 trace 不记录每个 barrier 的资源参数，尚不能从 13 次汇总中精确拆出可消除的 buffer-only 批次数量。

## 明确不需要逐次查询的原生调用

两种队列策略的下列数字完全相同：

| API | 每帧次数 | 应归属的生命周期 |
|---|---:|---|
| GetDescriptorHandleIncrementSize | 1075 | Device 与 descriptor heap type |
| GetCPUDescriptorHandleForHeapStart | 991 | 实际 descriptor heap 实例 |
| GetGPUDescriptorHandleForHeapStart | 84 | 实际 shader-visible heap 实例 |
| 合计 | 2150 | 无需在每次绑定时重新查询 |

[CopyDescriptor / CopyPersistentDescriptor / GetGpuHandle](../../src/SomeEngine.Graphics.Direct3D12/CommandBindings.cs:1850) 当前反复调用这些查询。应由设备保存描述符步长，由堆实例保存自身起始句柄，创建替换堆时建立新值。已有 [DescriptorPage](../../src/SomeEngine.Graphics.Direct3D12/DescriptorStorage.cs:135) 已把步长存为字段，同一后端的另一条路径却仍逐项查询。这个判断不依赖冻结相机、物体、灯光或材质值。

2150 次查询在异步样本中合计约 0.136 ms；调用数量很多，但并非原生 6 ms 的主要耗时。应同时区分次数优化和提交成本优化。

## 描述符复制与状态重绑

每帧 740 次 CopyDescriptorsSimple 在两种队列策略下也相同。RG 的 [SetParameterBindings](../../src/SomeEngine.RenderGraph/FrameRecording.cs:615) 走 transient 绑定路径；[SetTransientParameterBindingsSlow](../../src/SomeEngine.Graphics.Direct3D12/CommandBindings.cs:269) 每次分配 descriptor 区间并逐项复制。现有列表内相同绑定检查包含普通数据，因此只改变普通数据也可能进入重新复制描述符的路径。已存在的 persistent 绑定路径也会向当前列表的临时 descriptor 区间逐项复制，不能只换 API 名称便宣称消除了复制。

应让描述符表跟随实际资源、布局和 GPU 可见区间的生命周期，动态普通数据单独更新。740 次中有多少 descriptor 内容跨帧相同还需要身份/内容证据，不能把 740 次全部称为冗余。当前每次复制一个 descriptor 的 API 组织也可在保持布局与生命周期正确的前提下做批量复制。

列表拆分会连带增加 allocator Reset、list Reset/Close，并重置命令状态。SetPipeline 已有同 pipeline 跳过机制，root table 也有列表内相同值检查；所以不能说 RHI 完全没有去重。问题是列表结束会清掉状态，而更换 pipeline 时又重设 root signature、清除绑定状态并写默认表。共享 layout 的不同 pipeline 是否可以保留绑定，需要按实际 root signature 身份判断。

## 同步的剩余冗余条件

每次 Backend.Submit 都调用 [SignalCompletionUnderGate](../../src/SomeEngine.Graphics.Direct3D12/CommandLifecycle.cs:378)，所以 41 次提交直接带来 41 次 completion signal。这里的 signal 还支持资源回收和跨队列消费者；应减少不必要提交边界，而不是随意删 signal。

RG 的 AddCompletion 已在单次提交内对同一源队列取最大值，但没有把目的队列此前已经建立的等待依赖延续用于后续提交。[ExecuteCompletionWaits](../../src/SomeEngine.Graphics.Direct3D12/CommandLifecycle.cs:395) 会逐项发出 Wait。对于同一目的队列、同一单调 completion fence，已经排入的更大等待可以覆盖后续较小等待；已明确完成的值也不再需要等待。这些是队列执行中已成立的事实，不需要把场景输入做成通用缓存。当前 raw trace 没有 fence 身份和数值，因此不声称 38 次中具体有多少重复。

RHI 另有大 barrier batch 超过栈容量阈值后退化成逐项 Barrier 的路径。不过这份异步记录中嵌套 RhiBarriers 数为 0，该逐项路径未触发，不能将当前 178 次主线程原生 Barrier 都归因于阈值退化。

这里“与场景数量无直接关系”指固定图功能、pass 和资源布局时不随实体逐个增长。新增材质种类、功能、资源或流送任务仍可能改变图及绑定规模，本次没有把 1024→1 实例的场景扫点冒充为已完成实验。

原始复算产物：[异步分组](F:/.codex/research/render-execution-cost-20260906/command-audit/async.json)、[关闭异步分组](F:/.codex/research/render-execution-cost-20260906/command-audit/graphics.json)、[只读 C# 复算工具](F:/.codex/research/render-execution-cost-20260906/command-audit/Main.cs)。
