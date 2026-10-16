# Typed animation consumer

此独立程序集通过公开 API 执行动画、ECS 与视图行为，并提供纯算法和完整字段路径测量。消费者自己的值运算、缓动、Spring、带托管历史的算法和批次算法定义在 `ConsumerMotions.cs`。

默认执行字段、字节、生成通道、managed 查询与系统入口、强类型 `.Animate`、引用 `State.Value`、算法生命周期、时钟、阶段与事件检查。ECS 与 Views 的行为源文件由测试工程和本消费者共同编译。

## 执行

本机通过公共 FIFO 提交构建、运行与 NativeAOT 发布。PowerShell 以字符串传递 MSBuild 属性。

```powershell
& ./tools/Invoke-Benchmark.ps1 dotnet build tools/SomeEngine.Animation.Checks/SomeEngine.Animation.Checks.csproj -c Release '--property:PublishAot=false' '-m:1' '-nodeReuse:false' '--property:UseSharedCompilation=false'
& ./tools/Invoke-Benchmark.ps1 dotnet tools/SomeEngine.Animation.Checks/bin/Release/net10.0/SomeEngine.Animation.Checks.dll
```

测量使用同一发布配置和固定 JIT 配置，队列保存提交进程的环境。纯批次入口为 `--benchmark`；完整路径入口为 `--full-benchmark`。`--full-benchmark --smoke` 使用 1,000 项检查测量流程，完整入口使用 1,000 / 10,000 / 100,000 项。

```powershell
$env:DOTNET_TieredCompilation = '0'
$animationChecks = Join-Path $PWD 'tools/SomeEngine.Animation.Checks/bin/Release/net10.0/SomeEngine.Animation.Checks.exe'
& ./tools/Invoke-Benchmark.ps1 $animationChecks --benchmark
& ./tools/Invoke-Benchmark.ps1 $animationChecks --full-benchmark
```

NativeAOT 使用相同消费者与输入，发布时生成方法映射以核对代码体积。

`--codegen` 独立执行静态 `IEasing` Tween、Spring、消费者 History、标量函数回调和引用字段批次。Release 使用 `DOTNET_JitDisasm` 记录这些具体方法，NativeAOT 使用同一次发布的方法映射与反汇编核对入口、内循环和字节数。此入口与性能采样分别运行。

```powershell
& ./tools/Invoke-Benchmark.ps1 dotnet publish tools/SomeEngine.Animation.Checks/SomeEngine.Animation.Checks.csproj -c Release -r win-x64 --self-contained true '--property:PublishAot=true' '--property:IlcGenerateMapFile=true' '--property:OptimizationPreference=Speed' '-m:1' '-nodeReuse:false' '--property:UseSharedCompilation=false' -o tools/SomeEngine.Animation.Checks/bin/Release/net10.0/win-x64/native-publish
$nativeChecks = Join-Path $PWD 'tools/SomeEngine.Animation.Checks/bin/Release/net10.0/win-x64/native-publish/SomeEngine.Animation.Checks.exe'
& ./tools/Invoke-Benchmark.ps1 $nativeChecks
& ./tools/Invoke-Benchmark.ps1 $nativeChecks --full-benchmark
```

## 测量范围

`BatchMeasurements` 使用 64 次预热、96 次采样，比较相同参数、状态和逐项时间的直接 C# 循环与公共批次入口。每条结果包含中位数、P95/P99、每批分配、实例大小、首次数组分配和数组保留字节。

`--function-benchmark` 追加同公式 `SquareEasing.Sample` 的静态类型 Tween 与标量函数适配比较，供已有纯批次结果继续补充。完整 `--benchmark` 同时包含这两种情况；完整字段路径也分别记录 `typed-square` 与 `function-square`。函数适配在每次未完成采样时通过参数中保存的地址调用一次缓动。

`FullPathMeasurements` 使用 12 次预热、48 次采样、固定 `1/120` 秒增量和 `1,000,000` 目标值。三类字段为普通字段、嵌套值字段、引用对象内的可写属性。Tween、Spring 与消费者 History 分别通过公共 `AnimationSystems.Add` 管线运行，采样后检查轨道的持续活动与实际字段结果。

常规 World 使用直接同步执行。名称带 `worldjobs` 的三类 Tween 字段使用 `WorldJobs` 与 zero-worker、Checked scheduler，单列引擎存储访问和调度集成的完整成本。默认行为检查也覆盖这种 World 的单来源及多来源写回。

完整结果的每项单位按类别确定：`ecs` 为系统调用、查询调用或组件字段；`full`、`control`、`lifecycle` 和 `graph` 为目标/实例；`source` 为贡献来源，每个字段含两个来源。`first_ns` 和首次分配表示建立数据的成本，保留堆内存在预热后的完整 GC 观察；`warmup_allocated_bytes` 包含首次执行与容量增长。进程峰值工作集跨同一次进程中已执行的场景累积。`executableBytes` 记录当前进程映像大小，Release 的 apphost 与 NativeAOT 的本机映像分别解释。

系统入口与查询委托/函数指针入口分别测量。组件读取、取得整列类型化字段写视图，以及逐实体 World 字段写入分别测量。控制包含已声明算法切换、重定目标、停止与再次启动；生命周期包含首次声明、首次播放、首次完成帧和同一实例的完成后复用。

来源包含固定关系、公共 `Contribute` 提交的当帧动态来源，以及完成输出在输入保持、层权重变化和基准值变化时的合成。图使用一次编译、多实例共享定义，固定序列、收敛序列、状态机和 Blend1D 分别记录。图推进结果包含当前输入提交、Timeline 与 GraphSystem；求值结果包含类型化轨道、图输出、合成、图收尾与时间线完成。

`memory` 行记录单通道 Tween、同一通道预声明 Tween 与 Spring，以及包含 64 个浮点历史值和托管引用的消费者状态。每组记录声明和首次激活的耗时、分配、GC 后保留堆，以及通过公开缓冲视图取得的轨道与具体算法容量。实际元素大小同时列出，供默认内联容量取舍使用。

每个队列任务在返回的结果目录保存 `stdout.log`、`stderr.log` 和 `result.json`。交付证据集中于 `wiki/evidence/Animation/2026-10-16`，HUD 的整帧、GPU 与呈现测量由该目录的固定 HUD 工作量页面定义。

`--allocation-check` 复用单组 1,000 项普通 Tween 的完整公开路径。完整字段测量在预热后收集保留堆，`allocation` 行单列完整 GC 后首个样本和随后 47 个样本的实际分配，原有均值保留这 48 个样本的总量。

发布后的 `Read-NativeCode.ps1` 读取同一次 NativeAOT 方法映射，输出代表方法的代码字节、静态数据字节、PDB 地址和映像分节。以这些地址调用 LLVM `llvm-objdump --disassemble --start-address=... --stop-address=...`，即可核对最终映像中的实际指令。该诊断同样通过公共队列运行。

```powershell
$nativeDirectory = Join-Path $PWD 'tools/SomeEngine.Animation.Checks/bin/Release/net10.0/win-x64/native-publish'
$nativeMap = Join-Path $PWD 'tools/SomeEngine.Animation.Checks/obj/Release/net10.0/win-x64/native/SomeEngine.Animation.Checks.map.xml'
$nativeCodeReader = Join-Path $PWD 'tools/SomeEngine.Animation.Checks/Read-NativeCode.ps1'
$symbolTask = (& ./tools/Invoke-Benchmark.ps1 pwsh -NoProfile -File $nativeCodeReader -NativeDirectory $nativeDirectory -MapPath $nativeMap -LlvmBinDirectory 'C:/Program Files/LLVM/bin') | ConvertFrom-Json
$nativeSymbolsPath = Join-Path $symbolTask.OutputDirectory 'stdout.log'
$nativeInstructions = Join-Path $PWD 'tools/SomeEngine.Animation.Checks/Read-NativeInstructions.ps1'
& ./tools/Invoke-Benchmark.ps1 pwsh -NoProfile -File $nativeInstructions -SymbolsPath $nativeSymbolsPath -NativeDirectory $nativeDirectory -LlvmBinDirectory 'C:/Program Files/LLVM/bin'
```

`Read-NativeInstructions.ps1` 按符号采集任务记录的映像地址和方法长度，输出最终 PE 中的代表批次、字段写入、系统回调与 Timeline 启用读取指令。两个诊断任务沿共同 FIFO 顺序执行，使用同一次发布的映像与符号。
