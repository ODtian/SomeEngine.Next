# 渲染命令优化交付：2026-09-06

已实现 [命令数量放大审计](Render-Command-Amplification-2026-09-06.md) 中的提交合并、late release 聚合、固定描述符信息复用、描述符内容复用与批量复制、同根签名绑定保留、completion 等待去重和大 barrier 批量路径。正式测量保持当前帧输入更新、GPU 提交和完成等待在当前帧内，三个输入的仅帧标记耗时分别下降 19.3%、21.3%、11.8%。这些是整帧经过时间的实测收益，不能解释成全部 RG/RHI 管理开销已被消除。

## 整帧测量

同一台 Intel Xeon E5-2698B v3 / RTX 3080，驱动 32.0.16.1074，Release / .NET 10 / D3D12 / 1280×720 / hidden window / 无 VSync。每个输入运行仅帧标记和完整区间（含分配计数）两种模式，各三次独立进程，每次 512 帧、排除前 256 帧，每个表格单元是 768 个帧的算术均值。基线为此前已保存的正式测量，优化后重新采集 18 组；不是交错运行旧、新二进制的配对实验。正式计时关闭 GPU 验证和图像读回，验证另跑。

| 输入 | 仅帧标记：之前 → 之后 ms | 降幅 | 完整插桩：之前 → 之后 ms | 降幅 |
|---|---:|---:|---:|---:|
| 动态场景 / 异步计算 | 12.074 → 9.742 | 19.3% | 13.212 → 10.512 | 20.4% |
| 动态场景及材质 / 异步计算 | 12.474 → 9.821 | 21.3% | 13.580 → 10.580 | 22.1% |
| 动态场景 / 关闭异步计算 | 10.967 → 9.671 | 11.8% | 12.000 → 10.106 | 15.8% |

三次进程均值范围如下。改善覆盖了本次各次运行；运行波动、JIT 和驱动状态仍限制小差异解释。

| 输入 | 仅帧标记之前 | 仅帧标记之后 | 完整插桩之前 | 完整插桩之后 |
|---|---:|---:|---:|---:|
| 动态场景 / 异步计算 | 11.554–12.637 | 9.719–9.781 | 13.052–13.394 | 10.128–11.088 |
| 动态场景及材质 / 异步计算 | 11.901–13.154 | 9.767–9.915 | 13.163–14.153 | 10.530–10.665 |
| 动态场景 / 关闭异步计算 | 10.745–11.338 | 9.541–9.744 | 11.789–12.306 | 10.053–10.198 |

完整插桩相对仅帧标记的均值差由之前 1.139 / 1.106 / 1.033 ms 变为 0.770 / 0.759 / 0.435 ms。调用区间减少也减少采集扰动；不把完整插桩收益全部归因于产品，也不从仅帧标记耗时中扣完整插桩的分类耗时。

## 独占时间

单位 ms/帧，下面均取完整插桩。RG、RHI 已扣除子区间，不重复计入原生。原生表示 API 内经过时间，包含驱动及阻塞，不等于 GPU 执行时间或 CPU 忙碌采样。工作线程合计单列，不能再次加入或扣除整帧延迟。

| 输入 | RG 主线程 | RHI 主线程 | 原生主线程 | 显式等待 | 工作线程合计 |
|---|---:|---:|---:|---:|---:|
| 动态场景 / 异步计算 | 3.436 → 2.863 | 1.527 → 1.166 | 6.465 → 4.750 | 0.165 → 0.075 | 0.368 → 0.000 |
| 动态场景及材质 / 异步计算 | 3.484 → 2.822 | 1.553 → 1.138 | 6.587 → 4.694 | 0.169 → 0.091 | 0.370 → 0.000 |
| 动态场景 / 关闭异步计算 | 4.054 → 3.127 | 1.195 → 0.895 | 4.298 → 2.925 | 0.827 → 1.487 | 0.955 → 0.505 |

动态异步主线程 RG+RHI 从 4.962 降至 4.029 ms，仍有约 4 ms 被测管理成本。关闭异步时显式等待从 0.827 升至 1.487 ms，整帧仍缩短；CPU 更早完成提交会改变等待占比，不能要求每个区间同时下降。动态异步 RG 分配约 281421 B/帧、RHI 41720 B/帧，接近此前；本次主要减少命令组织和原生调用，没有宣称消除行更新或状态提交分配。

## 命令数量

这里按 raw trace 执行祖先区分普通录制、普通提交和 late release，合计全部线程。基线取原审计的各一份正式记录，之后取三份正式记录的均值；除等待随已完成 fence 状态波动外，下表核心数量在三次优化后采集中一致。

| 项目（每帧） | 异步：之前 → 之后 | 关闭异步：之前 → 之后 |
|---|---:|---:|
| 执行 pass | 107 → 107 | 107 → 107 |
| 普通提交批次 | 28 → 25 | 17 → 13 |
| 普通命令列表 | 71 → 25 | 48 → 15 |
| 单 pass 普通列表 | 51 → 9 | 28 → 2 |
| late release 列表 / 提交 | 13 → 2 | 7 → 2 |
| 原生命令列表 / allocator Reset / list Reset / Close | 84 → 27 | 55 → 17 |
| ExecuteCommandLists / Queue.Signal | 41 → 27 | 24 → 15 |
| Queue.Wait | 38 → 24.047 | 24 → 12.013 |
| 固定描述符查询合计 | 2150 → 0 | 2150 → 0 |
| 描述符复制 API 合计 | 740 → 62 | 740 → 62 |
| SetDescriptorHeaps | 50 → 20 | 33 → 11 |
| SetComputeRootSignature | 61 → 58 | 60 → 57 |
| CreateConstantBufferView | 80 → 49 | 80 → 49 |
| CreateShaderResourceView | 294 → 107 | 294 → 107 |
| CreateUnorderedAccessView | 64 → 32 | 64 → 32 |
| 原生 Barrier | 186 → 175 | 160.668 → 155.668 |
| BatchConstruction 区间 | 64 → 0 | 36 → 0 |

描述符复制从 740 次逐个 CopyDescriptorsSimple 变为 44 次 CopyDescriptors + 18 次 CopyDescriptorsSimple，总 API 次数 62，下降 91.6%。62 是调用次数，不是实际描述符数量；批量调用可复制多个内容。设备步长及 CPU/GPU 堆起点三项稳态查询合计由 2150 变为 0，堆创建或替换时仍会正常查询。

动态异步 Queue.Wait 的三次均值为 22.105 / 25.785 / 24.250，由目的队列已有等待和实际完成值决定，不应写成固定次数。检查完成值会增加 GetCompletedValue 查询，这是省略已完成 GPU Wait 的真实成本。

异步普通批次的 pass 数均低于现有并行阈值，优化后该场景无需工作线程录制；关闭异步仍有 2 次 LatencyWork、26 个工作线程 pass。并行录制能力保留，不能将测量中的零 job 解释为删除了功能。

## 实现与边界

- [RG 录制和提交](../../src/SomeEngine.RenderGraph/FrameRecording.cs)：沿拓扑序合并同一实际队列的提交，在追加 pass 会使批次依赖成环时保留边界；包含访问依赖及资源复用依赖。该算法是已经事先说明的贪心合并，不承诺全局最少批次数。移除 acquire 触发的额外列表切分，两种提交模式共用批次录制；录制结构未变时保留分组。每个录制单元使用自己的命令上下文，让其 shader-visible heap 的内容与 GPU 完成后的帧实例复用一致。
- 当前帧初始资源交接的 late release 在普通 GPU 工作前按源队列聚合，异步从 13 次到 2 次、关闭异步从 7 次到 2 次。必要 release barrier、源 completion 和消费者等待保留；没有预测下一帧输入。
- [设备和队列](../../src/SomeEngine.Graphics.Direct3D12/Device.cs)：保存设备描述符步长与各堆起点，堆替换时更新；completion 等待按实际目的队列和单调源 completion fence 复用已建立的最大等待，已完成值跳过。外部 timeline 可以回退，继续逐次等待。
- [绑定](../../src/SomeEngine.Graphics.Direct3D12/CommandBindings.cs)：普通数据变化单独上传；相同实际 root signature 的 pipeline 切换保留允许保留的状态。实际 shader-visible 区间保存来源描述符对象及内容，内容相同才省复制；资源、视图、来源回收重用、位置或堆变化按实际情况更新。改变的 CPU 描述符按堆类型批量复制。
- 描述符增长路径原先把 shader-visible heap 用作 CopyDescriptors 的 CPU 来源，这不符合 D3D12 要求。现在从保留的 CPU 来源重建有效区间，持久描述符发布记录也保留 CPU 来源；增长及 GPU 读回测试覆盖了这一修复。
- [Barrier 批量](../../src/SomeEngine.Graphics.Direct3D12/CommandBarrierBatches.cs)：大于栈阈值时使用 ArrayPool，保留一次原生 Barrier，不再逐项退化。当前场景原本未触发大批量退化，不能把整帧收益归于这项修复。
- [验证层](../../src/SomeEngine.Graphics.Validation/ValidationLayer.Barriers.cs)：合并列表后暴露了原有同步验证过严的问题。D3D12 要求后继 SyncBefore 覆盖前序 SyncAfter，验证改为展开聚合范围后检查包含关系；Access、Layout 和 split barrier 配对要求保留，遗漏必要阶段仍拒绝。依据 [微软 enhanced barrier 同步规范](https://microsoft.github.io/DirectX-Specs/d3d/D3D12EnhancedBarriers.html#synchronization) 与 [CopyDescriptors 文档](https://learn.microsoft.com/en-us/windows/win32/api/d3d12/nf-d3d12-id3d12device-copydescriptors)。

## 正确性与验证

所有 18 份正式记录 Complete=true、DroppedEvents=0、Misuse=0，可追踪工作帧外耗时为 0。输入计数与基线逐项一致：MovingMeshes=1024、MovingLights=3、ChangingMaterials=0 或 1。相机、网格、灯光和材质继续使用当前帧值。全部完整记录每帧仍执行 107 pass、39 Dispatch、35 ExecuteIndirect、6 DrawInstanced、36 CopyBufferRegion、3 CopyTextureRegion，与原审计一致。计数证明工作命令没有被删，不能单独证明每个 GPU 像素完全相同，因此另外执行验证和读回。

- `SomeEngine.slnx` 最后 Release 构建：0 警告、0 错误。
- 全解决方案测试：2783 总计，2781 通过、1 跳过、1 失败。唯一失败是已有 ECS 测试 `BufferJobAccessTests.WarmedBufferAdmission_DoesNotAllocatePerCallback`，buffer 观察到 3024 B；此前基线也在同一测试失败（3744 B），本次没有修改该 ECS 路径。没有将全套测试报告成全绿。
- RG 测试 74/74 通过，包含两种提交模式 × 可合并/必须保留依赖边界，跨 Graphics/Compute/Copy 队列读取结果，验证三次或四次必要提交。
- [D3D12 复用测试](../../tests/SomeEngine.Graphics.Direct3D12.Tests/WarpCommandReuseTests.cs) 覆盖常量连续变化、资源/视图替换、CPU 描述符回收复用、堆在 reset 时替换、2/129 元素数组、录制中增长后的两份 GPU 读回、255/257/600 个 barrier 仍一次原生调用、目的队列等待隔离和外部 fence 回退。
- 最后同步验证修正后，验证及复用定向测试 49/49 通过，包括扩大前置同步范围允许和漏掉阶段拒绝。全套 2783 测试在该局部修正前运行；最后构建与 49 项定向测试在其后运行。
- 32 帧 motion/async、materials/async、motion/关闭异步三种 GPU 验证与帧输出读回全部完成，输出 1280×720 非空。前两种在最终同步验证局部修正前通过，关闭异步在修正后通过。验证读回添加额外 pass，验证日志为 113 pass，正式性能采集为 107 pass。输出 hash 分别为 291524DBA305A9FB、828A18C63F37B404、E0380BB84BCD4E57；不据此宣称不同运行逐像素相同。

## 数据与复现

[完整派生 JSON](Render-Command-Optimization-2026-09-06.json) 保存前后均值、分位数、逐次均值、分类时间/次数/分配、原生祖先分组、输入计数、正式命令和二进制 SHA-256。

- 基线：[正式记录](F:/.codex/research/render-execution-cost-20260906/measurements-event/runs.json)、[原命令审计](F:/.codex/research/render-execution-cost-20260906/command-audit/async.json)。
- 优化后：[正式记录](F:/.codex/research/render-command-optimization-20260906/measurements-verified/runs.json)、[二进制 SHA-256](F:/.codex/research/render-command-optimization-20260906/measurements-verified/assemblies.json)、[复算脚本](F:/.codex/research/render-command-optimization-20260906/finish-report.py)、[原始命令分组](F:/.codex/research/render-command-optimization-20260906/command-audit/motion-async-full-1.json)。
- 验证：[构建日志](F:/.codex/research/render-command-optimization-20260906/build-validation.log)、[全套测试](F:/.codex/research/render-command-optimization-20260906/solution-tests.log)、[最后定向测试](F:/.codex/research/render-command-optimization-20260906/validation-tests-final.log)、[关闭异步 GPU 验证](F:/.codex/research/render-command-optimization-20260906/final-validation-graphics-2.log)。

正式采集示例（仓库根目录，Release 构建完成后执行；仅帧标记换成 `--trace-frames-only`，材质输入换成 `materials`，关闭异步增加 `--no-async-compute`）：

```powershell
& ./src/SomeEngine.Runtime/bin/Release/net10.0/SomeEngine.Runtime.exe --frames 512 --hidden-window --no-vsync --dynamic-scene --synchronous-frames --scene-playback motion --fixed-step 0.016666667 --trace F:/capture.setrace --trace-detail --trace-allocations
& ./tools/SomeEngine.Trace/bin/Release/net10.0/SomeEngine.Trace.exe analyze F:/capture.setrace --warmup 256 --json F:/capture.json
```

这次没有使用旧 harness 工作流，没有创建数值 run 目录。中途探索采集及首次关闭异步验证失败日志保留在证据目录中，但未混入最终正式 18 份记录。剩余的合法提交边界、必要描述符更新、实时场景数据上传和资源状态写回仍然执行；本报告不声称整个渲染链已达理论最优。
