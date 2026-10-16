# Typed animation consumer

此独立程序集通过公开 API 执行动画、ECS 与视图行为，并提供纯算法和完整字段路径测量。消费者自己的值运算、缓动、Spring、带托管历史的算法和批次算法定义在 `ConsumerMotions.cs`。

默认执行字段、字节、生成通道、managed 查询与系统入口、强类型 `.Animate`、引用 `State.Value`、算法生命周期、时钟、阶段与事件检查。ECS 与 Views 的行为源文件由测试工程和本消费者共同编译。

## 执行

本机通过公共 FIFO 提交构建、运行与 NativeAOT 发布。PowerShell 以字符串传递 MSBuild 属性。

```powershell
& F:\SomeEngine.Next\tools\Invoke-Benchmark.ps1 dotnet build tools/SomeEngine.Animation.Checks/SomeEngine.Animation.Checks.csproj -c Release '--property:PublishAot=false' '-m:1' '-nodeReuse:false' '--property:UseSharedCompilation=false'
& F:\SomeEngine.Next\tools\Invoke-Benchmark.ps1 dotnet tools/SomeEngine.Animation.Checks/bin/Release/net10.0/SomeEngine.Animation.Checks.dll
```

测量使用同一发布配置和固定 JIT 配置，队列保存提交进程的环境。纯批次入口为 `--benchmark`；完整路径入口为 `--full-benchmark`。`--full-benchmark --smoke` 使用 1,000 项检查测量流程，完整入口使用 1,000 / 10,000 / 100,000 项。

```powershell
$env:DOTNET_TieredCompilation = '0'
& F:\SomeEngine.Next\tools\Invoke-Benchmark.ps1 tools/SomeEngine.Animation.Checks/bin/Release/net10.0/SomeEngine.Animation.Checks.exe --benchmark
& F:\SomeEngine.Next\tools\Invoke-Benchmark.ps1 tools/SomeEngine.Animation.Checks/bin/Release/net10.0/SomeEngine.Animation.Checks.exe --full-benchmark
```

NativeAOT 使用相同消费者与输入，发布时生成方法映射以核对代码体积。

```powershell
& F:\SomeEngine.Next\tools\Invoke-Benchmark.ps1 dotnet publish tools/SomeEngine.Animation.Checks/SomeEngine.Animation.Checks.csproj -c Release -r win-x64 --self-contained true '--property:PublishAot=true' '--property:IlcGenerateMapFile=true' '--property:OptimizationPreference=Speed' '-m:1' '-nodeReuse:false' '--property:UseSharedCompilation=false' -o tools/SomeEngine.Animation.Checks/bin/Release/net10.0/win-x64/native-publish
& F:\SomeEngine.Next\tools\Invoke-Benchmark.ps1 tools/SomeEngine.Animation.Checks/bin/Release/net10.0/win-x64/native-publish/SomeEngine.Animation.Checks.exe
& F:\SomeEngine.Next\tools\Invoke-Benchmark.ps1 tools/SomeEngine.Animation.Checks/bin/Release/net10.0/win-x64/native-publish/SomeEngine.Animation.Checks.exe --full-benchmark
```

## 测量范围

`BatchMeasurements` 使用 64 次预热、96 次采样，比较相同参数、状态和逐项时间的直接 C# 循环与公共批次入口。每条结果包含中位数、P95/P99、每批分配、实例大小、首次数组分配和数组保留字节。

`FullPathMeasurements` 使用 12 次预热、48 次采样、固定 `1/120` 秒增量和 `1,000,000` 目标值。三类字段为普通字段、嵌套值字段、引用对象内的可写属性。Tween、Spring 与消费者 History 分别通过公共 `AnimationSystems.Add` 管线运行，采样后检查轨道的持续活动与实际字段结果。

常规 World 使用直接同步执行。名称带 `worldjobs` 的三类 Tween 字段使用 `WorldJobs` 与 zero-worker、Checked scheduler，单列引擎存储访问和调度集成的完整成本。默认行为检查也覆盖这种 World 的单来源及多来源写回。

完整结果的每项单位按类别确定：`ecs` 为系统调用、查询调用或组件字段；`full`、`control`、`lifecycle` 和 `graph` 为目标/实例；`source` 为贡献来源，每个字段含两个来源。`first_ns` 和首次分配表示建立数据的成本，保留堆内存在预热后的完整 GC 观察；`warmup_allocated_bytes` 包含首次执行与容量增长。进程峰值工作集跨同一次进程中已执行的场景累积。`executableBytes` 记录当前进程映像大小，Release 的 apphost 与 NativeAOT 的本机映像分别解释。

系统入口与查询委托/函数指针入口分别测量。组件读取、取得整列类型化字段写视图，以及逐实体 World 字段写入分别测量。控制包含已声明算法切换、重定目标、停止与再次启动；生命周期包含首次声明、首次播放、首次完成帧和同一实例的完成后复用。

来源包含固定关系、公共 `Contribute` 提交的当帧动态来源，以及完成输出在输入保持、层权重变化和基准值变化时的合成。图使用一次编译、多实例共享定义，固定序列、收敛序列、状态机和 Blend1D 分别记录。图推进结果包含当前输入提交、Timeline 与 GraphSystem；求值结果包含类型化轨道、图输出、合成、图收尾与时间线完成。

`memory` 行记录单通道 Tween、同一通道预声明 Tween 与 Spring，以及包含 64 个浮点历史值和托管引用的消费者状态。每组记录声明和首次激活的耗时、分配、GC 后保留堆，以及通过公开缓冲视图取得的轨道与具体算法容量。实际元素大小同时列出，供默认内联容量取舍使用。

每个队列任务在返回的结果目录保存 `stdout.log`、`stderr.log` 和 `result.json`。交付证据集中于 `wiki/evidence/Animation/2026-10-16`，HUD 的整帧、GPU 与呈现测量由该目录的固定 HUD 工作量页面定义。
