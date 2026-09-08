**Render enhancement 性能深度复核 — 2026-09-05**

本次结论：当前工作树已有可验证的局部优化，但在本机的实际运行场景中仍然受明显的 CPU 渲染开销限制。最值得优先处理的是灯光表的 CPU 清零上传、RenderGraph 纹理同步展开与合并，以及 GPU 灯光网格的串行工作分配。仅继续压缩 tessellation payload 或提高图分析命中率，不能解决已测出的主要帧时问题。内存方面，已有约 145 MiB 的统计并不代表实际 transient 堆容量。

本报告是独立测量与源码复核，没有修改产品源码，也不宣称已实现下面的优化建议。[机器可读结果](F:/SomeEngine.Next-render-enhancement/wiki/architecture/Render-Enhancement-Performance-Review-2026-09-05.json)与[完整逐帧统计](F:/.codex/research/render-performance-review-20260905/wall-time-summary.json)保留了测量口径、所有重复轮次及原始数据路径。

**研究对象和测量边界**

- 分支：`codex/render-enhancement`；HEAD `a787000034e7eba7559d20299a2c704216444011`；main / merge base `b1d59a82835e2b044c193835dd3967960afe555f`。
- 已提交分支相对 main 为 15 个提交、435 个文件变化；测量开始时工作树另有 429 条 Git 状态记录，包含新增基准工具和今天的优化。报告研究的是这份工作树，不把未提交结果归到 HEAD 本身。
- 对 1,066 个产品源码、资产、工具和基准输入记录 SHA-256，原始 Git 状态另存；测量完成后核对这些输入及原有 Git 状态。见[输入散列](F:/.codex/research/render-performance-review-20260905/source-hashes.json)和[复核结果](F:/.codex/research/render-performance-review-20260905/source-verification.json)。
- 本机：Xeon E5-2698B v3，16 核 / 32 线程，RTX 3080，驱动 610.74，Windows 11 build 26200，.NET SDK 10.0.111，高性能电源方案。CPU 是较老的平台，结果不能直接推广到现代桌面 CPU。没有锁定 GPU 时钟，也没有排除所有桌面后台活动。
- Release、D3D12、1280×720、隐藏窗口、无 VSync，保留引擎默认三个在途帧。性能轮次关闭 validation、逐帧输出回读和图诊断；抓帧、采样、构建与对照测量分开执行。
- 帧时记录从渲染循环入口到 `Present` 调用完成，包括在途帧等待、事件处理、游戏抽取等待、Prepare、图录制与提交。它是这套运行条件下的 CPU 帧循环墙钟时间，不是显示器扫描输出时间或 GPU timestamp 帧时。`--hidden-window` 的结果也不能直接代替前台窗口端到端延迟。
- 独立插桩源码和运行副本保存于研究目录。运行时测量行预分配；图分段测量每帧有一个很小的记录数组，故 allocation 数包含这部分插桩成本。原有零分配基准使用其普通构建副本。
- 未做同画质、同功能的 main 分支完整渲染 A/B；当前分支增加的 VSM、材质和细分功能改变了工作负载，因此不报告“整个分支比 main 快多少”。

GPU validation 会修改 shader 并注入额外工作，还可能改变 copy queue 的执行形式；功能验证与性能数字必须分开。[Microsoft 的 GPU-based validation 文档](https://learn.microsoft.com/en-us/windows/win32/direct3d12/using-d3d12-debug-layer-gpu-based-validation)

**实际帧循环：局部优化尚未转化为低帧时**

默认静态四种组合每种独立启动三次，正序 / 反序 / 正序运行，每次 512 帧、丢弃前 256 帧。额外场景每种启动两次、反转顺序，每次 384 帧、丢弃前 192 帧。合计 9,984 帧，其中 4,992 帧进入稳态统计。下表的主值是各轮中位数的中位数；P95 给出各轮范围，未挑选最快一轮。

| 场景与开关 | 轮数 | 中位数 ms | 各轮中位数 ms | 各轮 P95 ms |
|---|---:|---:|---:|---:|
| 默认 1024 实例，hybrid，Cluster async 开 | 3 | 43.44 | 43.08–45.28 | 67.63–73.91 |
| 默认 1024 实例，hybrid，Cluster async 关 | 3 | 39.73 | 39.62–40.00 | 50.60–53.90 |
| 默认 1024 实例，全 HW，Cluster async 开 | 3 | 39.45 | 38.50–40.73 | 47.90–54.02 |
| 默认 1024 实例，全 HW，Cluster async 关 | 3 | 39.61 | 39.16–39.83 | 51.75–55.32 |
| 默认 1024 实例，动态，hybrid / async 开 | 2 | 45.47 | 45.28–45.67 | 68.06–68.85 |
| 新生成位移场景，hybrid，Cluster async 开 | 2 | 49.31 | 49.18–49.44 | 71.00–74.16 |
| 新生成位移场景，hybrid，Cluster async 关 | 2 | 41.63 | 40.29–42.98 | 57.35–58.49 |
| 新生成位移场景，全 HW，Cluster async 开 | 2 | 42.35 | 41.27–43.43 | 57.32–59.56 |
| 128 实例、单实例运动，hybrid / async 开 | 2 | 44.40 | 43.99–44.80 | 67.27–67.64 |

默认 hybrid 的 Cluster async 开启后，主值比关闭时高约 **9.36%**；新生成的位移场景高约 **18.43%**。这表明当前调度在这台机器、这些场景下没有带来帧循环收益，不能据此否定所有硬件和复杂场景的 async compute。必须先处理 CPU 同步和录制成本，再依据实际 GPU 时间线决定哪些通路适合重叠。

`--no-async-compute` 只影响 Cluster 内的队列选择，包括 shade、SW raster 和部分 dice 通路；Lighting 等功能仍可位于 compute queue。这不是全引擎单队列运行。源码见[Cluster shade 队列选择](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.Render.Cluster.Pipeline/ClusterPipelineSystem.Passes.cs:2847)与[通用计算 pass 队列选择](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.Render.Cluster.Pipeline/ClusterPipelineSystem.Passes.cs:2933)。全 HW 在本例较快也不构成普遍的 raster 策略结论。

动态场景按墙钟推进运动，不同进程没有固定相同动画姿态。因此动态行是运行状况观察，不用来计算相同姿态的静态/动态增量成本或做逐像素等价声明。不同 raster / async 组合也未做最终颜色逐像素一致性证明；VSM 页竞争本身可能改变细页驻留集合。

**CPU：分析缓存命中了，真正的重成本仍每帧执行**

默认 hybrid / async 开的三轮稳态分段中位数约为：

| 图内操作 | 耗时 ms |
|---|---:|
| 展开本帧 rows | 0.28 |
| 检查分析复用输入 | 0.069 |
| 重新分析依赖与调度 | 约 0，稳态命中复用 |
| 资源及 view 物化 | 1.93 |
| 同步状态与 barrier 处理 | 15.22 |
| 录制、调度、提交及状态提交 | 24.00 |

每项分别取中位数，不要求相加等于整帧中位数。首轮 512 帧中复用 505 帧；单独开启诊断的 160 帧中复用 153 帧。稳态正常帧有 129 个声明 pass、105 个存活 pass、143 个 buffer、24 个 texture、955 条 access、903 条依赖与 793 条 barrier 诊断记录。渲染线程每帧分配中位数约 **843 KB（约 823 KiB）**，含很小的测量记录开销，且不统计其它线程的分配。

因果边界很清楚：[FrameExecutor.Execute](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.RenderGraph/FrameExecutor.cs:161) 在复用分析后仍调用 `Materialize` 和 `ResolveSynchronization`，再录制与提交。[完整静态快速路径的条件](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.RenderGraph/FrameExecutor.cs:124) 排除了动态上传、外部绑定与 swapchain 等输入；这次真实帧始终未走该路径。针对完全静态图的微基准不能替代本例。

一次单独的 EventPipe 采样按 `FrameExecutor.Execute` 调用栈范围筛选，而不是把所有等待中的 worker 累加后当 CPU 占用。该范围的 inclusive 样本中，`RecordAndSubmit` 约 58%，`ClearBuffer` 约 18.6%，底层 `CommitPattern` 约 18.0%，`CompactRanges` 约 18.7%，`Scheduler.Complete` 约 12.1%。这些有父子重叠、包含等待，不能相加，也不能直接当作删除某个函数就能获得的帧时收益。[调用栈分析](F:/.codex/research/render-performance-review-20260905/trace-analysis.json)、[原始采样](F:/.codex/research/render-performance-review-20260905/profile-default.nettrace)。

**优先问题一：灯光表每帧走 CPU 逐字节清零和上传复制**

[LightingClearPassData.Record](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.Render.Features/Lighting/LightingFeature.cs:482) 无条件清完整 grid 与完整 index list。[D3D12 ClearBuffer](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.Graphics.Direct3D12/CommandCopies.cs:206) 为清零范围预留上传内存；[CommitPattern](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.Graphics.Direct3D12/CommandBindings.cs:712) 用逐字节循环写 pattern，然后调用 `CopyBufferRegion`。

默认 tile=16、depth slices=16、每 cell 64 个 uint 索引。在 720p，grid 有 80×45×16=57,600 个 cell；grid 为 0.439 MiB，索引表为 14.0625 MiB，总计 **14.502 MiB / view / frame**。同一配置在 1080p 约 32.871 MiB，4K 约 130.518 MiB。这是源码直接决定的 CPU 填充量及随后 GPU copy 的数据量，不是实测 DRAM/PCIe 带宽，也不含其它 clear。

消费者[只按 cell 中的有效 count 读取索引](F:/SomeEngine.Next-render-enhancement/assets/Shaders/standard.slang:176)，构建 shader 对每个有效索引写入具体 light id。优先方向是让 GPU 并行初始化必要计数，避免为未读取的固定容量索引表逐帧清零；必须同步证明并声明有效写入范围、处理溢出与内容定义规则，不能只删掉 clear 让图错误地接受未定义读取。若保留通用 buffer pattern 清零功能，也应单独评估 CPU 填充方式及适用队列，而非让图形功能的大块清零自动落入按字节上传路径。

**优先问题二：纹理范围先大量展开，再进行昂贵的合并**

两次独立的更细插桩得到同样的稳态条数：合并前 **4,186 条普通纹理 barrier + 28,706 条纹理跨队列转移记录**；普通纹理 barrier 合并后为 64 条。合并本身分别约 **8.01 / 8.51 ms**，完整同步阶段约 15.25 / 15.82 ms。最后图诊断报告的 793 项还包括 buffer、queue acquire/release 等类型，不能拿它和前面的普通纹理数当成同一种计数。

[ResolveTextureSynchronization](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.RenderGraph/FrameSynchronization.cs:368) 对每次 access 的 mip / array layer 单元展开状态与队列转移；[CompactTextureBarriers / CompactRanges](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.RenderGraph/FrameBarrierCompaction.cs:9) 随后用带资源引用的值类型、字典分组和多次列表写入合并。采样与分段计时同时确认这里是热点，已经超出“看起来有很多循环”的推断。

优先方向是保留可合并的范围结构，在生成同步结果时减少不必要的逐单元对象搬运，复用不变的同步拓扑；状态、completion 和资源实例变化仍需更新。必须保留不同 producer、queue、layout、split-phase、atomic winner 合并的边界语义，不能简单把所有层压成一个 barrier。[两次细测原始数据](F:/.codex/research/render-performance-review-20260905/detail-graph.json)、[重复轮次](F:/.codex/research/render-performance-review-20260905/detail-repeat-graph.json)。

**优先问题三：GPU 灯光网格的工作分配严重限制并行度**

这次从新构建的 Release 运行副本抓取第 100 帧，等待应用退出后重放七次。使用 RenderDoc 1.43 `EventGPUDuration`，它通过事件前后的 GPU timestamp 计算时长。[RenderDoc 对应版本源码](https://raw.githubusercontent.com/baldurk/renderdoc/v1.43/renderdoc/driver/d3d12/d3d12_counters.cpp)

| GPU 事件类别 | 默认 1024 实例 ms | 新生成位移场景 ms |
|---|---:|---:|
| BuildClusteredLightGrid | 2.6766 | 10.0812 |
| ClusterShade | 1.1663 | 0.0850 |
| Tessellation split，24 次合计 | 无 | 0.1693 |
| Tessellation dice，4 次合计 | 无 | 0.0451 |
| 全部计数事件的重放时长之和 | 5.1828 | 11.4141 |

重放事件总和不是有真实队列重叠的整帧墙钟，不能取倒数宣称 FPS，也不能与上面的 CPU 帧时相减得到“空闲时间”。这里仅用它定位本次抓帧中的热点构成。下一步测真实队列重叠应使用独立时间线采集；[PIX Timing Captures 的官方说明](https://learn.microsoft.com/en-us/windows/win32/direct3dtools/pix/articles/timing-captures/pix-timing-captures)解释了它如何同时观察 CPU/GPU 提交、执行与 residency。

GPU 回读确认默认场景有 3 盏局部光，新位移场景只有 **1 盏**；二者均为 57,600 cell、每 cell 64 索引。[shader](F:/SomeEngine.Next-render-enhancement/assets/Shaders/clustered_light_grid.slang:110) 一线程负责一盏光，超出 light count 的线程立即退出，随后单线程串行执行 slice×tileY×tileX 循环和原子写入。新位移场景的点光 range=40，覆盖范围很大，所以“只有一盏光”并不意味着这个 kernel 很轻。

需要让一盏大光覆盖的 cell 工作能够分散到多个线程组，并测少量大光、多量小光、溢出和视锥边界场景。现有证据支持工作分配问题；没有采集 occupancy、寄存器或 cache-miss 计数，不能把它进一步包装成已测出的某种硬件瓶颈。新位移 capture 中光网格约占重放事件总和的 88.3%，继续优化约 0.17 ms 的 split 难以改变这部分的主导地位。

默认 capture 的 90 个计数事件中有 26 个零尺寸 indirect dispatch；位移场景 170 个中有 57 个。它们提示还有固定调度开销，但优先级低于上述已经量化的多毫秒热点。不应为了跳过空 dispatch 引入 CPU 同步回读。

[默认场景新抓帧](F:/.codex/research/render-performance-review-20260905/capture-default_frame100.rdc)、[位移场景新抓帧](F:/.codex/research/render-performance-review-20260905/capture-displacement_frame100.rdc)、[事件原始数据](F:/.codex/research/render-performance-review-20260905/gpu-fresh-analysis.json)。截图中的帧率文本受抓帧和 UI 的 delta clamp 影响，不用于本报告帧率判断。

**内存：约 145 MiB 是逐资源统计，不是实际堆容量**

[FrameDiagnostics](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.RenderGraph/FrameDiagnostics.cs:38) 对每个活跃 transient resource 的 `Placement.Size` 求和，texture 也使用相同逻辑。这个口径不去重 heap/offset，且没有计 heap 的未使用容量、其它在途帧或其它资源系统。即使 placement 重叠复用，也不能靠这个总和直接得到真实峰值。

两次 160 帧默认静态运行都得到以下结果：

| 口径 | 数值 | 含义 |
|---|---:|---|
| 单帧 resource placement 大小求和 | 144.875 MiB | 原诊断 `151912448` bytes |
| 各帧当前引用的 device-local transient 堆 | 512 / 640 / 640 MiB | 按 heap 对象去重，包含堆内空余空间 |
| 三个在途帧引用的 device-local 堆合计 | **1,792 MiB** | 共 14 个 128 MiB 堆 |
| 三个在途帧引用的 upload 堆合计 | **96 MiB** | 每帧一个 32 MiB upload 堆 |

这里是从最近三个帧的分配结果可达的 heap 对象及 `Heap.Info.Size` 直接读取，**不是整个进程的 resident VRAM，也不是全部 graph 堆的完整清单**：未被最近分配引用的闲置 heap、persistent 资源、RHI 自有上传/描述符、资产、驱动及 swapchain 都不在这张表中。不能把单帧 145 MiB 和三帧 1,792 MiB 相除宣传同口径膨胀倍数。

[allocator 的 device-local 最小堆为 128 MiB、upload 为 32 MiB](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.RenderGraph/ResourceAllocators.cs:848)，并按兼容 flags 分池。固定大页粒度、多个兼容性池及三个在途帧，会使结构体少几十 MiB 的收益未必转化成少申请一个 heap。应分别报告资源容量、去重后的使用区间、实际 heap 容量、在途帧总量和设备 residency，再决定分配粒度与复用改进；不能直接降低 VSM 页预算来换一个更好看的显存数字而不计画质影响。[内存原始记录](F:/.codex/research/render-performance-review-20260905/detail-memory.json)、[重复记录](F:/.codex/research/render-performance-review-20260905/detail-repeat-memory.json)。

**既有优化中，哪些结论仍成立**

本次重跑了仓库的 sparse buffer range benchmark，64 个 reader、每个 reader 128 个离散区间、16 帧 warmup、48 帧测量、四轮交替顺序。普通逐 range 声明注册中位数 0.749–0.768 ms、执行 9.065–11.687 ms；共享集合注册 0.0073–0.0077 ms、执行 0.596–0.742 ms。四轮全部保留，首轮普通路径明显更慢，没有只引用后面较快的结果。调用线程分配分别约 18,552 / 18,656 B，并非零分配。[本轮基准 JSON](F:/.codex/research/render-performance-review-20260905/range-rerun.json)

这足以确认共享范围集合在重复区间压力场景下的价值，但该基准没有真实灯光清零、VSM 多层纹理、呈现和完整材质工作；约 0.6 ms 的执行值不能被用作真实游戏帧的 RenderGraph 成本。

历史的四材质分组实验原始 JSON 仍支持 split 调度 96→24 次、split 重放中位数 0.419584→0.108416 ms（下降 74.16%）。重新汇总同批 capture 的所有计数事件，总和中位数仅从约 11.6161→11.1667 ms，变化约 3.87%，而且它仍不是实际帧时，也不是严格最终画面相同的对照。历史 depth、visibility、motion 相同，但颜色和 VSM 细页驻留集合不同；原报告已披露这一点。[历史数据独立汇总](F:/.codex/research/render-performance-review-20260905/gpu-event-analysis.json)

split work 24→16 B、final patch 24→20 B、raster bin 8→4 B、shade work 8→4 B，以及从半页尺寸生成 VSM Hi-Z，是有明确容量或工作量价值的改动。但容量节省不自动等于带宽、堆容量或整帧同比下降。新抓帧中最显著的 GPU 问题已经转向灯光网格。

历史报告的位移场景为 16,422 个 final patch、192 个 quality-limited；本轮使用当前 `SomeEngine.RenderScenarios` 从新副本生成的位移场景，验证第 24 帧得到 **1,452 patches、0 dropped、0 limited**。两者场景配置不应视为同一份冻结输入，不能把这组差别称为新取得的性能或质量提升。今后的对照需同时固定源码、资产/配置散列与动画时间。

**建议的处理优先级与验收依据**

| 优先级 | 工作 | 应当观察的结果 |
|---|---|---|
| 1 | 消除灯光索引表无效全容量清零，处理 CPU byte fill / upload 路径 | 默认与 4K 的 CPU clear 时间、每帧上传字节下降；有效索引与溢出语义不变 |
| 1 | 减少纹理同步逐 cell 展开后的复制、排序和合并 | 真实默认/VSM 帧的同步阶段显著低于目前约 15 ms；跨队列、array/mip、history 和 atomic hazard 验证通过 |
| 1 | 让单盏大光的 cell 遍历真正并行执行 | 一盏大光约 10 ms 的 kernel 耗时下降，少光/多光场景均正确；独立测真实 GPU 时间线 |
| 2 | 修正内存度量，审查 128 MiB 堆粒度、兼容池与多帧累计 | 资源容量变化能对应到 heap 容量和 residency；不能用单帧 placement 求和代替 |
| 2 | 重新评估 Cluster async 通路及 CPU 录制粒度 | 修复上述 CPU 问题后，重新在相同画质和多硬件上做开关对照；不以“有多个 queue”作为收益证明 |
| 3 | 减少无工作时仍存在的固定 passes / dispatches，评估缓存与物化成本 | 看真实帧的 CPU 与 GPU 总成本；避免为跳过空工作引入同步回读或新的长期保活存储 |

这些是基于本轮证据的建议，没有承诺未经实施与复测的收益百分比。首要回归集合应包含真实 present/import/upload 帧、大量 VSM array layer、少量大光、多量小光、hybrid/HW 和动态 caster；仅重复微基准会漏掉此次最主要的瓶颈。

**验证与复现**

本轮默认、位移、WPO/alpha、单实例运动四个场景各做 24 帧 GPU validation + 输出回读，共 96 帧，进程全部成功退出。四组最后一帧 `tessDropped=0`；当前生成的位移场景 `tessLimited=0`。默认大场景只需驻留 2/6 个已登记页，剩余未请求页不等于流送失败；日志的 missing 与 load failure 不能混为一谈。本轮未额外把 VSM fine/coarse allocation failure 导出为新的完整容量对照，因此保留历史报告的页池不足边界，不宣称已解决。

普通 Release solution 构建通过。为恢复所有普通构建产物，串行完整重建使用 `-m:1 -p:BuildInParallel=false`，结果 0 warning / 0 error；一次并行 Rebuild 出现 SlangShaderSharp 输出文件的清理/引用冲突，随后串行重建通过。采样构建不能用于零分配断言；最初复用插桩产物的测试结果作废，最终普通源码完整重建后的 Release RenderGraph 测试 **57/57 通过，0 skipped**。原始日志保留在研究目录。

原始测量工具：

- [帧循环插桩生成](F:/.codex/research/render-performance-review-20260905/instrument.py)、[图分段插桩生成](F:/.codex/research/render-performance-review-20260905/instrument_graph.py)、[堆与 barrier 细测插桩](F:/.codex/research/render-performance-review-20260905/instrument_detail.py)。
- [默认四路三轮运行](F:/.codex/research/render-performance-review-20260905/run_matrix.py)、[额外场景运行和功能验证](F:/.codex/research/render-performance-review-20260905/run_scenarios.py)、[统计脚本](F:/.codex/research/render-performance-review-20260905/summarize.py)。
- [新 GPU 抓帧与七次重放](F:/.codex/research/render-performance-review-20260905/capture_gpu.py)、[GPU 数据汇总](F:/.codex/research/render-performance-review-20260905/analyze_gpu.py)、[限定 RenderGraph 调用栈的采样分析](F:/.codex/research/render-performance-review-20260905/analyze_trace.py)。
- [普通 solution 完整重建日志](F:/.codex/research/render-performance-review-20260905/rebuild-solution-serial.log)、[普通 RenderGraph 测试日志](F:/.codex/research/render-performance-review-20260905/test-rendergraph-clean.log)。

复现时必须使用同份工作树和资产。当前研究目录保留 baseline 插桩运行副本 `runtime/`、独立生成的场景，以及额外细测副本 `detail-runtime/`。先运行相应构建，再按脚本从原始源码生成测量副本；不要把 `detail-runtime` 的分配或诊断成本混入 baseline。运行脚本会覆盖同名结果，保留本次证据时应先使用新的输出目录。

正式性能结论目前限于 RTX 3080 / D3D12 和上述场景。尚缺跨厂商、Vulkan、现代 CPU、4K 实测、重型多材质/多 view、完整 VSM 压力分布、无捕获 GPU 队列时间线以及与 main/UE 的等价场景对照。上述缺口不影响已定位的源码路径与本机测量事实，但限制了平台通用结论和整分支倍速声明。
