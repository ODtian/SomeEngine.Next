# 关系查询原始证据

本目录保存配对测量的原始精度数据。每个 series 的 `index.json` 将 case、实际源码提交、后端、比较结果、设备、内存与公共队列的完整 result/stdout/stderr 放在同一条目。`.json.gz` 解压得到运行器原样写出的 JSON，包括每帧 ticks、E/H 或 H/E 轮序、输出数量、接触次数/深度、全线程/当前线程分配、GC、Managed/WorkingSet、冷初始化、首查询、真实阶段边界及分布统计。

最终 C# 源码为 `c0effa290eee225c296f779a85b9d79109d789a5`。[逐 case 最新完整覆盖](focused-native-r45/latest-case-coverage.json)按实际 `FinishedAtUtc` 选择每个场景的最新完整记录，关联各自 source、series、原始 JSON、metrics 和 Required 结果；[CSV](focused-native-r45/latest-case-coverage.csv)与[来源计数](focused-native-r45/latest-case-coverage-summary.json)保存同一映射。完整矩阵及后续定向回归共同形成 108/108 结果等价、原严格口径 114/126、批准口径 126/126。

| 最新记录来源 | 场景数 | Required 指标数 | 原始报告 |
| --- | ---: | ---: | --- |
| `51c8ce75` · r38 完整矩阵 | 93 | 110 | [完整原始系列](final-native-r38/summary.md) |
| `393a9fbf` · r39 定向反馈 | 9 | 10 | [十四场反馈](focused-native-r39/summary.md) |
| `5b6c96e8` · r41 回归反馈 | 4 | 4 | [六场反馈](focused-native-r41/summary.md) |
| `c0effa29` · r45 并行反馈 | 2 | 2 | [两场反馈](focused-native-r45/summary.md) |

最终两项 `ScheduledPairOutput` 均通过原严格口径。`threads-small-sparse-parallel` 的 median 为扩展 555 / 手写 742.5 ticks，比值 `0.7474747474747475`；p95 为扩展 883.25 / 手写 1225 ticks，比值 `0.7210204081632653`。`threads-small-uniform-parallel` 的 median 为扩展 1735 / 手写 2176 ticks，比值 `0.7973345588235294`；p95 为扩展 2637.75 / 手写 3307 ticks，比值 `0.7976262473540974`。[同源两项结果](focused-native-r45/approved-summary.json)与[执行边界](focused-native-r45/queue-boundaries.json)保存原值。两场景执行累计 0.8260352 秒、报告执行 5.2236288 秒、提交至报告完成 1558.4311124 秒；公共队列案间间隔为 438.3546043 秒，准备和观察边界均通过。最终[八包消费方证明](final-packages-r45/package-codegen-proof.md)对应 `c0effa29`：14 个实际生产阶段全部成功，JIT、NativeAOT 执行与选定 IL、四个 Native 函数共同关联同一组产物。

`51c8ce75949cf16d7190e14028c655f526d5697b` 的完整批次作为基线保留：139 个场景任务与 5 个后处理任务均返回 Exit0。NativeAOT 完整矩阵的结果等价为 108/108；原严格口径通过 103/126，批准口径通过 114/126。当次 12 项余项保留实际比值、绝对 ticks、分配与 GC，见[同列表](final-native-r38/approved-unmet.md)。

| 系列 | 场景数 | Required 指标数 | Strict ≤1.00 | Approved ≤1.01 | 原始报告 |
| --- | ---: | ---: | ---: | ---: | --- |
| 51c NativeAOT 完整矩阵基线 | 108 | 126 | 103 | 114 | [完整表](final-native-r38/summary.md)、[批准口径](final-native-r38/approved-summary.json) |
| H1/H2 同函数对照 | 4 | 0 | 独立诊断 | 独立诊断 | [四项对照](final-hh-r38/control-summary.md) |
| JIT 九类代表 | 9 | 11 | 8 | 9 | [完整表](final-jit-r38/summary.md)、[批准口径](final-jit-r38/approved-summary.json) |
| 持续更新独立阶段 | 9 | 0 | 独立诊断 | 独立诊断 | [阶段表](final-phases-r38/summary.md) |
| 新进程启动 | 9 | 0 | 独立诊断 | 独立诊断 | [七对进程样本](final-startup-r38/report.md) |

51c 基线的 NativeAOT 各类批准通过数为 finite 15/18、default 4/9、character 10/12、bitmap-space 18/18、character-space 6/6、complete 18/18、updates 17/18、threads 17/18、consume 9/9。完整矩阵包含 59,904 个逐帧样本、1,512 个冷样本和 225 项分布；126 项 Required 与 99 项消费诊断分别保存。32 轮 × 8 帧形成每侧 256 个真实帧，保留 16 帧预热和 7 对冷执行。

51c H1/H2 与同源完整矩阵使用同一采样配置，Query 的 median/p95 比值分别为：small-mask1-mutual `1.0002514142049026 / 0.99437733118076044`，small-dynamic10 `1 / 0.97347198641765709`，large-mask8-mutual `1.0005149467068022 / 0.99354239542077416`，large-dynamic50 `1.0026074234880484 / 1.0045233144067844`。对照的 `Required=false`，其分布独立展示。

51c 完整批次的[执行边界与墙钟事实](final-validation-r38/README.md)关联准备任务、全系列 manifest、ready ACK、每项起止与五个后处理回执。[51c 八包消费方证明](final-packages-r38/package-codegen-proof.md)和[查询数据借用证明](focused-native-r38/native-query-binding.md)保留当时的实际产物与机器码。最终八包以 [r45 证明](final-packages-r45/package-codegen-proof.md)为准；[r39 六角色原生证据](focused-native-r39/native-output-value-excerpts.txt)、[r41 行与任务数据证据](focused-native-r41/native-confirmation-excerpts.txt)及 r45 的单文件分区回归分别关联其实际源码。早期 r32 与各 focused 系列继续作为各自源码的历史证据。

固定输入、规模、比例、预热、轮数和统计方法见[测量配置](../../ECS-Relations-Measurement-Configuration.md)。每一份原始报告同时保存自己的 Input 和 SourceCommit；每个 case 的执行前先对完整身份元组与消费数组逐项核对。持续更新核对覆盖八帧写入周期。

`summary.md` 使用 G17 格式显示原始比值。`metrics.csv` 保留双侧 median/p95/IQR/stddev、吞吐量和整帧全线程分配总数。`memory.csv` 按字段分别列出两侧实际 live/addressed/capacity payload 和数量。`cold.csv` 保留七对交替的 setup/index/first-query/first-consumption/total 原始 ticks。各阶段和主验收边界的定义保存在每份原始 JSON 的 Boundaries；分阶段诊断独立完成前置依赖后测量该阶段提交至完成。

读取一个压缩报告：

```powershell
$file = [IO.File]::OpenRead('finite-small-mask1-oneway.json.gz')
$gzip = [IO.Compression.GZipStream]::new($file, [IO.Compression.CompressionMode]::Decompress)
$reader = [IO.StreamReader]::new($gzip)
try { $report = $reader.ReadToEnd() | ConvertFrom-Json }
finally { $reader.Dispose(); $gzip.Dispose(); $file.Dispose() }
$report.Comparisons
```

同一 `.json.gz` 中 RawSamples 的计时与 Comparison 的原精度值直接对应。批准验收逐项使用扩展 median/手写 median ≤1.01 且扩展 p95/手写 p95 ≤1.01；原 `Passed` 字段继续按严格 ≤1.00 计算，`ApprovedRatioLimit=1.01` 与 `ApprovedPassed` 单独保存。统计与诊断数据分别保留，实际结果均包含自身后端与提交。

[报告导出脚本](../../../benchmarks/SomeEngine.ECS.Relations.Benchmarks/Export-Evidence.ps1) 保留原始 JSON 字节并压缩；[汇总脚本](../../../benchmarks/SomeEngine.ECS.Relations.Benchmarks/Write-MeasurementReport.ps1) 读取同一数据形成可读表格。实际构建与性能命令通过公共队列执行，完整命令见 index.Queue；[运行器提交脚本](../../../benchmarks/SomeEngine.ECS.Relations.Benchmarks/Submit-Measurements.ps1) 接收 QueueScript 和实际 SourceCommit。

受控完整系列同时保存queue-submission.json，将独占准备的起止、显式session、固定列表命令、实际源码和预排报告任务关联。逐case原始队列时间戳用于核对准备与实际测量的边界；早期反馈系列保留其原有提交方式和实际活动记录。

机器配置系列的Device.Machine与processors.csv保存实际进程/主线程mask、优先级、设置后CPU及每侧CPU端点汇总，RawSamples保留逐帧ProcessorBefore/After。该版allocation计数位于memory/CPU观察之外；r24及以前的FrameAllocatedBytes包含before-WorkingSet观察分配，历史记录继续保存原值。两版的elapsed时钟仍覆盖各自相同实际工作边界。

共享工作区系列以DataSharing和Workspaces记录实际数据持有：静态warm由双方顺序复用一份World/候选及pair/contact工作数组，持续更新与cold各自独立构造。workspaces.csv保存每份实际工作区的一次构造ticks，memory.csv以shared-workspace或各侧workspace行保存其容量；算法各自的树、摘要和成对输出资源继续列在Relation/Handwritten行。Verify在第二侧执行前保存第一侧完整真实行与消费值，原始结果核对沿此顺序进行。

逐帧配对系列的Input记录SideSwitchIntervalFrames=1、FramesPerSample=1，RawSamples按round→frame→双方顺序保存原始单帧时钟。controlled-shared-r27及以前报告保留其各侧连续8帧的原始块顺序，两类报告按各自完整系列独立判断。
