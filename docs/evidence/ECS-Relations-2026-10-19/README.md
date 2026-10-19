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