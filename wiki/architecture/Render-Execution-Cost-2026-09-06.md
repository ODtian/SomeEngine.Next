# RG / RHI / 原生执行成本：2026-09-06

本页是命令优化前的执行成本基线。后续命令优化及正式对比见 [渲染命令优化交付](Render-Command-Optimization-2026-09-06.md)。

统一 C# 插桩基础设施和独立无头分析工具已经用于本次测量。动态场景开启异步计算时，主线程 RG 独占约 3.44 ms、RHI 独占约 1.53 ms、原生调用约 6.47 ms。RG 与 RHI 当前仍有约 5 ms 的管理成本区间需要压缩；这些实测区间包含插桩和线程调度影响，不能宣称可以全部删除，也不是已经获得的优化收益。

本次交付新增采集、解析与测量能力，没有把下面的优化推演冒充为已经完成的 RG/RHI 算法优化。相关实现见 [[Engine-Trace]] 和 [工具说明](../../tools/SomeEngine.Trace/README.md)。

## 测量条件

- 本机 Intel Xeon E5-2698B v3、RTX 3080，驱动 32.0.16.1074；Release / .NET 10 / D3D12 / 1280×720 / hidden window / 无 VSync。
- 三种输入，每种完整插桩与仅帧标记各三次独立进程，共 18 份记录。每次 512 帧，排除前 256 帧，每个表格单元使用 768 个测量帧的算术均值；各项独占均值能够相加。
- `motion` 每帧移动全部 1024 个有运动配置的网格实例、相机和 3 个点光源；`materials` 额外每帧改变一个材质的 roughness。固定步长 0.016666667 秒，输入顺序一致。计数器逐帧记录 1024 / 3 / 0 或 1。
- `--synchronous-frames` 将当前帧场景更新、提取、录制、提交和 GPU 完成等待全部放在当前帧内。没有通过延后到下一帧或冻结输入改善数字。所有完整插桩记录丢事件=0、作用域错误=0、完整性检查通过；可追踪工作的帧外归属耗时=0。
- 仅帧标记模式关闭操作区间，保留帧边界和输入计数，用于估计测量扰动。它没有采集工作线程细分，不能将其输出的零值解释为没有后台工作。
- 正式耗时记录关闭 GPU 验证和图像读回。另一次 32 帧材质变化运行开启两项验证，通过 1280×720 非空输出检查，图像 hash `5A9AF56FB8486BE4`；它的高耗时不计入性能结果。
- 输入仍是可重复的引擎场景，不涵盖整个 AAA 游戏的蒙皮、完整场景拓扑修改和所有流送状态。任何复用机制都必须在那些输入变化时正确失效，不能由这组结果推导出它们永远不变。

## 独占分解

单位 ms/帧。RG 与 RHI 均已扣除其直接子区间，原生调用不会重复算入上层。工作线程一列是各工作线程独占经过时间之和，独立列出，不加到帧延迟。

| 输入 | 仅帧标记整帧 | 完整插桩整帧 | RG 主线程 | RHI 主线程 | 原生主线程 | 显式等待 | 应用、ECS、pass 等 | 工作线程合计 |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| 动态场景 / 异步计算 | 12.074 | 13.212 | 3.436 | 1.527 | 6.465 | 0.165 | 1.619 | 0.368 |
| 动态场景及材质 / 异步计算 | 12.474 | 13.580 | 3.484 | 1.553 | 6.587 | 0.169 | 1.786 | 0.370 |
| 动态场景 / 关闭异步计算 | 10.967 | 12.000 | 4.054 | 1.195 | 4.298 | 0.827 | 1.625 | 0.955 |

`app/ExecuteFrame` 独占包含当前尚未单独划分的渲染准备和提交调用方工作；不能把这个类别当作全部无关业务。`native` 是原生 API 调用边界内的经过时间，包含驱动内部工作及可能的阻塞，不等于 GPU 执行时间，也不等于采样得到的 CPU 忙碌时间。Win32 CPU fence wait 已单独归入 `wait`；队列 `Wait` 是提交 GPU 依赖的原生调用。

三次仅帧标记运行均值范围分别是 11.554–12.637、11.901–13.154、10.745–11.338 ms。完整插桩均值分别为 13.052–13.394、13.163–14.153、11.789–12.306 ms。这些波动限制了小于数十分之一毫秒的差异解释，不应过度解读表格保留的小数。

关闭异步计算在本机此输入下缩短整帧，但并不是完全相同的命令流：主线程 ExecuteCommandLists 从每帧 41 次变成 24 次、命令列表 Reset 从 80 次变成 48 次，更多录制工作落在工作线程，显式等待也增加。它是队列策略比较，不能归因成某一个 RG 函数变快，也不证明其他 GPU 或场景关闭异步计算更好。

## 成本集中位置

下面使用动态场景 / 异步计算完整插桩记录，每项是主线程独占均值。

| RG 区间 | ms/帧 | 对应行为 |
|---|---:|---|
| StateCommit | 0.727 | 提交后的资源边界状态计算与写回 |
| Rows | 0.603 | 行更新中除去各 SetRow 子区间后的剩余工作 |
| RecordSubmit | 0.530 | 波次、录制和提交组织中尚未下分的剩余工作 |
| Synchronization | 0.369 | 同步分析及更新的独占部分 |
| RowsSetViewRow | 0.191 | 463 次视图行设置 |
| Materialize | 0.179 | 物化层独占部分，底层资源创建另计 |
| BindingResolution | 0.171 | 75 次参数绑定解析 |
| RowsSetAccessRow | 0.140 | 696 次访问行设置 |

RHI 绑定 0.518 ms、其他命令管理 0.328 ms、barrier 管理 0.204 ms、命令生命周期 0.145 ms、提交锁内管理 0.092 ms。这些已经扣除原生调用。

原生开销主要是 ExecuteCommandLists 1.835 ms / 41 次、命令列表 Reset 0.832 ms / 80 次、队列 Wait 0.731 ms / 38 次、Signal 0.646 ms / 41 次、Barrier 0.520 ms / 178 次、Close 0.430 ms / 80 次，以及 Present 0.315 ms。原生调用总数约为主线程 4306 次、工作线程 249 次；并非全部都是昂贵 API，也不能因为某次调用短就断言无用。

主线程 RG 约分配 281641 B/帧，RHI 约 41720 B/帧。RG 中 Rows 约 120280 B、StateCommit 约 102120 B。材质变化额外使 ReconcileGraph 从约 0.0007 ms 上升到 0.151 ms、约 115802 B/帧，说明数值变化仍触发了可检查的上层重整成本；它没有被测量输入冻结。

## 可以如何复用

复用必须来自对象生命周期和明确的失效条件。下面的候选由当前源代码路径和区间成本共同支持，并不是按输入哈希添加一个通用缓存。

| 成本来源 | 结果应属于什么对象 | 何时失效 | 每帧仍必须做什么 |
|---|---|---|---|
| 多波次 BuildBatches / 录制工作划分 | 图结构中的队列、访问依赖和录制分组 | pass、队列、访问关系、录制模式改变 | 当前数据录制、必要提交和依赖值填充 |
| CommitPassState 重扫访问与区间 | 图资源访问区间及终态表达式 | 访问范围、读写模式、资源布局改变 | 写入本次提交实际 completion 值 |
| completion 改变触发边界再观察 | 资源的结构状态与本次完成值分别管理 | 结构状态变化才重建依赖结果 | 正确刷新当前 GPU 完成依赖，不能复用过期 fence |
| 参数 ID→View 查询和描述符表组织 | pipeline/layout 及实际资源或帧实例 | 资源身份、布局、范围、描述符内容变化 | 上传矩阵、灯光、材质值等动态内容 |
| 动态 overlay 的视图和访问行重建 | 具有真实存活期的视图和访问声明 | 创建、销毁、范围或访问关系变化 | 处理当前流送、变形和可见性产生的实际变化 |

依据见 [录制与提交](../../src/SomeEngine.RenderGraph/FrameRecording.cs)、[状态提交](../../src/SomeEngine.RenderGraph/FrameStateCommit.cs)、[完成依赖刷新](../../src/SomeEngine.RenderGraph/FrameSynchronizationChanges.cs)、[行更新](../../src/SomeEngine.RenderGraph/FrameRows.cs)、[RHI 绑定](../../src/SomeEngine.Graphics.Direct3D12/CommandBindings.cs)。

PSO、shader layout、仍然存活的资源视图通常具有跨帧生命周期，适合在创建或变更时建立结果。相机、物体、灯光、材质数值、蒙皮和流送内容则不能假设不变。减少重复描述符处理和提交次数可能继续降低原生开销，但需要逐项证明生命周期与依赖允许这种合并；不能将所有 native 时间当成已经达到最优的常数。

## 关于最优化边界

从当前测量能直接量化的管理成本区间是主线程 RG+RHI：动态异步 4.962 ms、材质变化异步 5.038 ms、关闭异步 5.249 ms。这是待压缩预算的上限候选，包含不可删除的必要管理、尚未细分的调度时间和采集扰动，不能保证全额获得。

做一个明确的乐观反事实：假定 RG/RHI 主线程自身时间全部归零，当前原生命令数量、各原生耗时、应用工作、等待及线程关系都不变，那么完整插桩表格中的剩余时间分别是 **8.250 / 8.542 / 6.751 ms**。计算只扣主线程独占项，没有把并行工作线程耗时再减一次。

这些数字既不是已实测的优化版，也不是真实下界。移除管理工作会改变 GPU 排队及 CPU 等待；减少提交又会改变原生调用总量。完整插桩还增加约 1 ms 扰动。因此不能将仅帧标记整帧直接减去完整插桩 RG/RHI，再把结果报成精确优化极限。当前证据支持“RG/RHI 仍有约 5 ms 的管理成本可研究压缩”，不支持“整条 CPU 渲染链已能达到 0.5–1 ms”的结论。

## 采集开销与数据完整性

三种输入完整插桩相对仅帧标记的整帧均值差为 1.139 / 1.106 / 1.033 ms，即约 9.4% / 8.9% / 9.4%。这同时受运行间波动影响，不能逐函数按比例扣除。

独立空作用域校准每种模式 250000 次、重复五次，仅校准进程设置 `DOTNET_TieredCompilation=0`。关闭采集的调用约 3.24–4.19 ns/次，未调用基线约 1.77–1.94 ns/次；开启采集约 143–152 ns/次，带分配计数约 96.8–100.2 ns/次，生产线程测量循环均为 0 B 分配、完整记录、零丢失。两个开启模式的不同代码生成和执行路径导致这个次序，不能推导出读取分配计数普遍使采集更快。

带分配计数时记录内的空区间均值约 43.6–45.2 ns；整次作用域调用还有区间之外的记录成本。不能只从 native 区间扣一个固定常数就声称获得无扰动原生耗时。普通事件块滚动没有产生线程分配；首次线程登记、启动缓冲分配与写盘线程不包含在 0 B 热路径结论内。

先前一组窗口轮询等待的原始实验保留在 `measurements/`。其 2 ms 轮询请求受到系统定时器粒度影响，使整帧落在约 15.6 ms 或更高。正式结果改用同帧 GPU 完成事件等待，位于 `measurements-event/`；没有混用两组数据，也没有把等待移出帧边界。

## 产物与复现

- [完整派生数据](Render-Execution-Cost-2026-09-06.json) 包含逐案例汇总、调用次数、分配、原始路径、运行命令、测量二进制 SHA-256 和空区间校准。
- [原始运行清单](F:/.codex/research/render-execution-cost-20260906/measurements-event/runs.json) 指向 18 份 `.setrace`、独立 C# 工具派生的逐帧 JSON 及日志；原始数据未覆盖。
- [汇总脚本](F:/.codex/research/render-execution-cost-20260906/aggregate-event.py) 只对 C# 工具已经完成的独占归账结果求均值，不在引擎内部放入分析规则。
- [图像/GPU 验证日志](F:/.codex/research/render-execution-cost-20260906/event-validation.log) 和 [校准原始结果](F:/.codex/research/render-execution-cost-20260906/calibration-steady/results.json)。

```powershell
dotnet build SomeEngine.slnx -c Release -m:1
src/SomeEngine.Runtime/bin/Release/net10.0/SomeEngine.Runtime.exe --frames 512 --hidden-window --no-vsync --dynamic-scene --synchronous-frames --scene-playback motion --fixed-step 0.016666667 --trace capture.setrace --trace-detail --trace-allocations
tools/SomeEngine.Trace/bin/Release/net10.0/SomeEngine.Trace.exe analyze capture.setrace --warmup 256 --json capture.json
```

`capture.setrace` 必须是新文件。材质压力输入替换为 `--scene-playback materials`；关闭异步计算加入 `--no-async-compute`；对照运行用 `--trace-frames-only` 替换 detail/allocations 开关。

## 验证状态

最终 `SomeEngine.slnx` Release 构建成功，0 警告、0 错误。

普通解决方案整套运行共 2771 项：2767 通过、3 失败、1 跳过。三项失败中的 D3D12 调用形状断言已修正：它现在沿引擎内的插桩转发检查最终 Silk 调用，继续禁止自行发出 `calli`；两项形状测试定向复跑通过。其余两项是加入本次插桩之前已出现的 ECS 失败，保留而未改动这些业务规则：

- `LifecycleClosureTests.WorldClosing_RejectsUnrelatedJobCallback`：期望 `ObjectDisposedException`，实际 `JobResourceSafetyException`。
- `BufferJobAccessTests.WarmedBufferAdmission_DoesNotAllocatePerCallback`：整套运行测得 buffer 路径 3744 B，零分配断言失败。

最终修正采集关闭时上下文读取的竞争后，Diagnostics 20/20、Job 209/209 再次通过；RG 在整套运行中 70/70、Runtime 19/19、Render 81/81、Cluster 139/139 通过。D3D12 的 420 项通过和 1 项形状失败原始结果保留，形状失败修复后的定向结果单独记录，未伪称重新执行过整套。跳过项因没有 Intel 显卡。

正式性能数据使用清单中保存的测量二进制哈希；之后的改动为格式整理、启动失败时句柄清理、CLI 错误提示、上下文读取竞争修复以及相应测试。最终二进制哈希及验证结果另存，不把不同构建声称为同一份二进制。最终版本另跑 32 帧材质回放、GPU 验证及图像检查，结果见 [最终运行日志](F:/.codex/research/render-execution-cost-20260906/final-context-validation.log)。

最终语义版本的验证记录含 647480 个事件、32 个线程，零丢失、零作用域错误、可追踪工作帧外归属时间为零；图像非空检查通过，hash 为 `1911CB6DD8E9E589`。此检查验证有效输出，不声称不同运行逐像素相同。此后仅整理了限定名与缩进，最终解决方案再次构建通过。

[整套测试日志](F:/.codex/research/render-execution-cost-20260906/final-solution-tests.log)、[最终 Diagnostics / Job 测试](F:/.codex/research/render-execution-cost-20260906/final-context-tests.log)、[最终原生形状测试](F:/.codex/research/render-execution-cost-20260906/final-native-shape-tests.log)、[最终构建日志](F:/.codex/research/render-execution-cost-20260906/final-format-build.log)。
