# 关系查询原始证据

本目录保存配对测量的原始精度数据。每个 series 的 `index.json` 将 case、实际源码提交、后端、比较结果、设备、内存与公共队列的完整 result/stdout/stderr 放在同一条目。`.json.gz` 解压得到运行器原样写出的 JSON，包括每帧 ticks、E/H 或 H/E 轮序、输出数量、接触次数/深度、全线程/当前线程分配、GC、Managed/WorkingSet、冷初始化、首查询、真实阶段边界及分布统计。

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

同一 `.json.gz` 中 RawSamples 的计时与 Comparison 的原精度值直接对应。验收逐项使用扩展 median/手写 median ≤1.00 且扩展 p95/手写 p95 ≤1.00。统计与诊断数据分别保留，实际结果均包含自身后端与提交。

[报告导出脚本](../../../benchmarks/SomeEngine.ECS.Relations.Benchmarks/Export-Evidence.ps1) 保留原始 JSON 字节并压缩；[汇总脚本](../../../benchmarks/SomeEngine.ECS.Relations.Benchmarks/Write-MeasurementReport.ps1) 读取同一数据形成可读表格。实际构建与性能命令通过公共队列执行，完整命令见 index.Queue；[运行器提交脚本](../../../benchmarks/SomeEngine.ECS.Relations.Benchmarks/Submit-Measurements.ps1) 接收 QueueScript 和实际 SourceCommit。

受控完整系列同时保存queue-submission.json，将独占准备的起止、显式session、固定列表命令、实际源码和预排报告任务关联。逐case原始队列时间戳用于核对准备与实际测量的边界；早期反馈系列保留其原有提交方式和实际活动记录。

机器配置系列的Device.Machine与processors.csv保存实际进程/主线程mask、优先级、设置后CPU及每侧CPU端点汇总，RawSamples保留逐帧ProcessorBefore/After。该版allocation计数位于memory/CPU观察之外；r24及以前的FrameAllocatedBytes包含before-WorkingSet观察分配，历史记录继续保存原值。两版的elapsed时钟仍覆盖各自相同实际工作边界。

共享工作区系列以DataSharing和Workspaces记录实际数据持有：静态warm由双方顺序复用一份World/候选及pair/contact工作数组，持续更新与cold各自独立构造。workspaces.csv保存每份实际工作区的一次构造ticks，memory.csv以shared-workspace或各侧workspace行保存其容量；算法各自的树、摘要和成对输出资源继续列在Relation/Handwritten行。Verify在第二侧执行前保存第一侧完整真实行与消费值，原始结果核对沿此顺序进行。

逐帧配对系列的Input记录SideSwitchIntervalFrames=1、FramesPerSample=1，RawSamples按round→frame→双方顺序保存原始单帧时钟。controlled-shared-r27及以前报告保留其各侧连续8帧的原始块顺序，两类报告按各自完整系列独立判断。
