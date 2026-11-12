# 性能测量

本页维护已实现的队列入口与使用方法。实现入口为 `tools/Invoke-Benchmark.ps1`、`tools/BenchmarkQueue/BenchmarkCommands.cs`、`CommandQueue.cs`、`RemoteCommands.cs` 和 `LinuxCommands.cs`。性能结论对应 workload、设备、backend、构建配置与计时边界；结果在会话中汇报，临时输出读取后清理。

## 提交与分发

```powershell
pwsh -NoProfile -File tools/Configure-BenchmarkRemote.ps1 -HostName 4090
pwsh -NoProfile -File tools/Invoke-Benchmark.ps1 --cpu dotnet run --project tools/SomeEngine.Job.Benchmarks -c Release
pwsh -NoProfile -File tools/Invoke-Benchmark.ps1 --remote --cpu dotnet run --project tools/SomeEngine.Job.Benchmarks -c Release
pwsh -NoProfile -File tools/Invoke-Benchmark.ps1 --gpu --timeout 60 <executable> <arguments>
pwsh -NoProfile -File tools/Invoke-Benchmark.ps1 --timeout 120 <executable> <arguments>
```

默认类别为 CPU；`--gpu` 和兼容选项 `--exclusive` 标记 GPU 任务。队列选项放在可执行文件之前。默认先申请远程资源；远程返回 `ResourcesBusy`，或 SSH 连接阶段发生 `IOException`、`TimeoutException` 时，命令进入本机资源调度。临时固定本机时使用 `--local` 或任务配置的 `Local: true`。调用脚本按实际执行主机选择构建参数和应用路径。`result.json` 用 `DispatchReason` 区分 `RemoteResourcesBusy`、`RemoteUnavailable` 和 `RequestedLocal`，连接失败时保留 `RemoteError`。

同一会话按 FIFO 完成，跨会话轮询；远程与本机共用这一顺序。会话标识依次来自 `--session`、`SOMEENGINE_BENCHMARK_SESSION`、`CODEX_THREAD_ID`、`CODEX_SESSION_ID` 和工作目录。执行器每两秒重新检查排队任务。

远程执行器通过 SSH 在 Linux 普通用户账户下运行，在同一资源锁中分配 CPU、GPU 和工作目录。Git 工作目录按 Git 文件列表同步，其它目录按提交工作目录中的文件同步；同步比较文件大小和修改时间，并复用编译输出。源目录绝对路径映射到远程工作目录。远程执行器设置 HOME、临时目录、NuGet 缓存和系统路径，并传递提交时的 .NET JIT 配置。

跨主机任务使用 `dotnet run` 或一条脚本命令共同完成构建与消费，使产物留在本次分配的工作目录。分开提交构建和执行的本机消费者使用 `--local`，沿提交时的同一工作目录读取产物；`SomeEngine.Animation.Checks` 的 Windows 发布与诊断采用这一方式。

## 基线与限额

普通性能运行使用正常工作负载的目标或公开参考实现作为门槛。队列通过 `--sample-pattern` 选择实际 stdout/stderr 中的数值，正则的命名组 `value` 表达采样；`--sample-max` 给出耗时等上限，`--sample-min` 给出吞吐等下限，数值单位与输出保持一致。每个有效采样都检查门槛，首次失标立即终止本机整棵进程树或远程 systemd 单元。

```powershell
pwsh -NoProfile -File tools/Invoke-Benchmark.ps1 --cpu --sample-pattern 'step_ms=(?<value>[0-9.]+)' --sample-max 16.67 <executable> <arguments>
pwsh -NoProfile -File tools/Invoke-Benchmark.ps1 --cpu --sample-pattern 'entities_per_second=(?<value>[0-9.]+)' --sample-min 1000000 <executable> <arguments>
pwsh -NoProfile -File tools/Invoke-Benchmark.ps1 --cpu --diagnostic '成员日志，前四步，开启阶段计时' <executable> <arguments>
```

`--diagnostic` 记录本次诊断的明确范围，关闭队列的采样目标终止，并通过 `SOMEENGINE_BENCHMARK_DIAGNOSTIC_SCOPE` 传给应用。诊断结果沿独立配置解释。`result.json` 保存 `SampleLimit`、`DiagnosticScope`、`FirstSample`、`FailedSample` 和 `StopReason`；目标失败使用退出码 1、`SampleTargetExceeded=true` 与 `StopReason=SampleTarget`。应用自身已有的场景预算检查继续沿相同目标执行。

| 项目 | 默认行为 |
| --- | --- |
| CPU | 固定 4 个物理核，包含其硬件线程；远程从同一 L3 缓存域选择空闲核心 |
| 线程配置 | .NET、OpenMP、OpenBLAS、MKL 默认使用 4 个线程 |
| 系统内存 | 8 GiB；Windows Job Object、Linux 用户级 systemd 服务约束整棵进程树 |
| 执行超时 | 默认 60 秒；`--timeout` 指定正整数秒，排队和源码同步另计；超时退出码 124 |
| 远程 GPU | 按利用率和剩余显存分配实际设备，每张卡同时分配给一条队列任务 |
| GPU 选择条件 | 利用率至多 10%，剩余显存至少达到 `GpuMemoryMiB` 加 512 MiB，候选空闲卡至少 2 张 |
| 工作目录 | 最多 3 个，每个使用 8 GiB 固定容量的 ext4 映像，通过用户态 FUSE 挂载 |
| SDK 缓存 | 固定 2 GiB 映像，复用 .NET SDK 10.0.112 |
| 标准输出 | stdout、stderr 各至多 8 MiB，超出部分继续收集并丢弃 |
| 结果保留 | 每次提交保留最近 8 个已完成结果；调用方读取后清理自己的结果目录 |

远程通过 Mesa Vulkan 设备选择层设置可见设备，执行前核对 Vulkan UUID 与分配的 GPU UUID。`GpuMemoryMiB` 默认 4096，是分配前的剩余显存要求；系统内存上限由操作系统执行。

结果记录实际 CPU 型号、GPU 型号和 UUID、核心编号、内存上限及退出状态。不同机器、型号和并发条件分别比较。换算估计经过对应 workload 的独立验证后，单独说明误差和适用范围。

本机 GPU 任务独占本机 GPU 资源，`Gpus` 保存系统枚举的设备名称、身份及硬件标识；Windows 使用系统显示设备枚举。远程任务分配一张 NVIDIA GPU，使用 `GpuModel` 和 `GpuId` 记录分配的型号与 UUID。

核心在命令开始前分配，并在整条进程树执行期间保持固定；每两秒的采样仅更新使用量。本机结果的 `ProcessorMask` 与远程结果的 `Processors` 记录这次分配。`ThreadEnvironment` 保存实际生效的 `DOTNET_*`、`COMPlus_*`、OpenMP、OpenBLAS 和 MKL 配置；构建配置和发布参数保存在 `Executable`、`Arguments` 中。`MemoryMiB`、`OutputMiB` 和 `TimeoutSeconds` 保存本次限额。

共享配置位于 `%LOCALAPPDATA%/SomeEngine/BenchmarkQueue/settings.json`。`SOMEENGINE_BENCHMARK_CONFIG` 指向的任务配置覆盖其中指定的字段，其余字段继承共享配置，因此单独调整 CPU 或内存时保留远程主机设置。支持 `RemoteHost`、`RemoteDirectory`、`CpuCoreCount`、`MemoryMiB`、`TimeoutSeconds`、`GpuMemoryMiB`、`MinimumIdleGpuCount` 和 `OutputMiB`。远程依赖用户级 systemd、cgroup v2 内存控制器、FUSE、NVIDIA 驱动和 Mesa Vulkan 设备选择层。

## 结果与清理

回执返回任务编号、会话、执行器进程编号和结果目录。结果位于 `%LOCALAPPDATA%/SomeEngine/BenchmarkQueue/results/<编号>/`，包含 `stdout.log`、`stderr.log` 和 `result.json`。

远程超出系统内存上限后终止任务，记录退出码 137 和 `MemoryLimitExceeded`。超时记录退出码 124 和 `TimedOut`。远程分配链路失败记录 `FailureStage=RemoteDispatch`；资源紧张后在本机执行的结果记录 `DispatchReason=RemoteResourcesBusy`。远程临时目录随任务清理，源码、编译输出和 SDK 保存在有容量上限的目录中。

渲染性能优化采用根级 AGENTS 规定的 DefaultGame 三场景平台与采样要求。原始结果和当次验证结论在会话中汇报。
