# Performance Evidence

性能结论必须对应明确 workload、设备、backend、构建配置与计时边界；证据页面可以按日期保存，但不得把一次 benchmark 的实现选择提升成长期架构规则。

当一个算法选择明确依赖性能数据时，architecture 页面只固定语义约束，具体候选和结论链接到相应 evidence/benchmark 记录。

## 当前三场景验收要求

1024 运动小球、Sponza、Amazon Bistro 的整帧 P95 **均须严格小于 1 ms**（`P95Ms < 1`）；等于 1 ms 不达标。这是尚须以最终源码和内容证明的验收要求，不是已经实现的性能结论。规格见 [#190](https://github.com/ODtian/SomeEngine.Next/issues/190)，统一反馈票为 [#81](https://github.com/ODtian/SomeEngine.Next/issues/81)。

当前三景配置统一使用 `MeasurementFrames.FirstFrame = 2048`、`FrameCount = 1024`，即每景采样第 2048–3071 帧；1024 小球保持运动。以实际生成并随应用发布的 `test-scenes.json` 核对配置和完整帧数，不回退到旧文档中的按秒采样窗口。整帧指标沿 presented-frame elapsed 计时边界；比较前冻结双方实际使用的 GPU、驱动、backend、分辨率、render path、NativeAOT/Satori、TAA/VSM/async、场景播放方式与时间步设置，并记录源码/二进制/内容身份。必须证明比较条件一致；不能把历史硬件、模式或旧基线身份当成本次已核实条件。改变窗口或其它条件的试验须单独标明，不能替代此验收。

`Complete=true`、三景 `Measured` 或采样完整仅表示反馈齐全；必须逐景检查整帧 P95 数值，不能把它们当作阈值已通过。构建、生成器探针、源码模型、局部行为测试、启动成功或 CPU/GPU 子阶段计时，也都不能替代最终三景验收。

统一入口 `tools/Measure-Scenes.ps1` 向公共 GPU 队列提交 `python -B tools/Measure-Scenes.py`。测量进程在执行机器上通过 MSBuild 查询 DefaultGame 的正常构建或 NativeAOT 发布目录；`-Managed` 选择托管诊断，默认选择 NativeAOT。应用参数 `--measure`、`--measure-trace` 在首个就绪呈现帧后调用 `SceneTestPlatform.StartMeasurements`，沿现有三景切换、预热、采样窗口与报告流程运行，报告写完后通过 `ApplicationScene.Exit()` 返回。

`-SceneConfigurationJson` 把已有场景配置文本透传给目标进程，测量作用域使用这份配置并在退出时恢复应用原配置；配置中的绝对内容目录可以复用执行机器上的同一份正式内容。显式 JSON 优先于客户端应用目录旁的配置读取。

`-PlatformConfigurationJson` 用于队列可能在不同操作系统执行的测量，JSON 按 Python `sys.platform` 的 `win32`、`linux` 分别提供 `ApplicationPath` 与 `Scenes`。执行端选择本机对应的应用和场景数组，再沿同一测量流程运行；这些值优先于单平台的应用与场景参数。两端分别复用各自的正式发布目录和内容，报告按实际设备与构建配置评估。

`-PublishSourceRevision <提交>` 让同一 GPU 队列任务在实际执行主机上先完成该提交的 NativeAOT/Satori 增量发布，再开始原有应用测量预算。该模式的任务时限为 1200 秒，构建单独计时。已完成本轮发布的主机可以在对应平台配置中提供从成功发布回执取得的 `PublishedHost`、`PublishedSourceRevision`，执行端匹配后直接复用该产物。其余执行主机按本机 RID 发布到所选应用原目录，随后沿相同三景、帧窗与报告检查运行。

Windows 测量进程使用 `WinSta0\Default` 桌面并置顶应用窗口。Linux 使用 Vulkan；已有 `DISPLAY` 时复用该显示，队列服务器通过 `tools/x11_display.py` 的 `open_display` 在所分配 GPU 上建立私有 Xorg 显示，并在进程结束后释放。报告中的 `GraphicsDevice`、`GraphicsDriver`、`GraphicsBackend` 和队列返回的 GPU 身份共同说明当次设备条件。

测量进程通过 stdout 的 `SCENE_REPORT` 和 `SCENE_FRAME_SAMPLES` 行交付完整 JSON，按应用随附 `test-scenes.json` 核对逐帧区间。设置 `SOMEENGINE_MEASUREMENT_OUTPUT_DIRECTORY` 和 `SOMEENGINE_MEASUREMENT_OUTPUT_BASENAME` 后，PowerShell 客户端把这两份 JSON 保存到指定的平铺输出位置；运行机器上的临时报告随测量释放。Trace 文件保存在当次发布目录的 `Performance` 子目录，`SCENE_DIAGNOSTICS` 输出其位置，供同一执行机器上的 Trace 工具读取后清理。

原 `docs/agents/acceptance-performance.md` 的长期验收要求迁移至本节；旧提交、原始测量与历史运行记录不在此重写，也不作为本次 rebase 后的通过证据。重新测量必须记录最终源码提交、对应内容版本、实际设备/backend、配置与采样边界；旧 shader/import 记录不能冒充新内容。

## 公共队列

CPU、GPU benchmark 通过 `tools/Invoke-Benchmark.ps1 <原命令及参数>` 提交到公共队列。默认任务和 `--cpu` 任务按 CPU 调度；使用 GPU 的任务通过 `--gpu` 提交，`--exclusive` 为其兼容写法。
同一会话按 FIFO 完成任务；跨会话的 CPU 任务和 GPU 任务按独立物理核心并行，GPU 任务之间独占 GPU。执行器按任务进程树的实际用核量动态分配核心，并通过 Windows Job Object 应用于整条进程树。构建、内容导入与捕获采用同一入口；性能比较记录实际核心分配，并保持工作负载、JIT 与处理器条件一致。

每条命令默认最多执行 60 秒，从进程启动开始计时，排队等待不计入。需要更长时间时，必须在可执行文件之前显式指定 `--timeout <秒数>`，例如 `--timeout 300`；只接受正整数秒，`0` 不表示无限等待。时限覆盖进程执行和输出收集。超时会请求终止整个进程树，结果记录退出码 `124`、`TimedOut: true` 和 `TimeoutSeconds`，然后继续处理后续任务。

```powershell
./tools/Invoke-Benchmark.ps1 dotnet run --no-build --project tools/SomeEngine.Animation.Checks -c Release -- --benchmark
```

提交返回任务编号和结果目录，自动启动隐藏的后台执行器。
目录保存 `stdout.log`、`stderr.log` 与 `result.json`，结果包含命令、工作目录、起止时间及退出码。
测量使用提交时的环境变量和参数分组。结果直接在会话中汇报；运行所需的临时输出放在项目外，使用完成后立即清理。

参见 [[evidence/RHI/Performance-Evidence-2026-08-20]] · [[architecture/cluster/Deform-Cache-Allocation]].
