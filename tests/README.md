# 功能回归范围

默认入口为 `just test` 或 `pwsh -NoProfile -File tools/Test.ps1`。入口先通过 `tools/Build.ps1` 生成 DefaultProject 的 shader 与场景内容、构建 `SomeEngine.slnx` 的全部项目，再运行 CPU 与 D3D12 WARP 公共行为测试。构建与测试配置统一为 Release。

GitHub Actions 的 `Software adapter tests` 在任意分支推送、PR 和手动触发时执行同一入口。PR 可以直接查看构建和测试状态。`Directory.Build.targets` 持有唯一的默认过滤条件；完成内容构建后，`dotnet test SomeEngine.slnx -c Release --no-build` 使用同一条件。显式 `--filter` 可选择独立类别。

`tools/Test.ps1` 将跨项目执行限制为最多两个 MSBuild 节点，各测试项目继续使用自身的并行设置与全部默认用例。固定的项目并发负载使公共同步点的挂起保护在 CI 和本地拥有一致的执行条件。

```text
(Category=Software)&(Category!=Hardware)&(Category!=LongFuzz)&(Category!=Performance)&(Isolation!=PresentationProcess)&(Isolation!=DeviceLossProcess)
```

每个测试程序集通过 `Xunit.AssemblyTraitAttribute` 获得类别。普通库和 WARP 测试为 `Software`，Vulkan 项目为 `Hardware`，基准工具测试为 `Performance`。特定用例通过 `Trait("Category", ...)` 标记额外类别。

同时提供软件、硬件参数的理论用例通过 `HardwareRowsTheory` 按数据行追加类别：Vulkan 原子缓冲用例和固定 WaveSize(32) 的网格压缩用例的 `true` 参数属于 `Hardware`，`false` 参数继续通过默认软件入口执行。两行分别保留独立的用例名称与断言。

| 类别与入口 | 所需环境与执行范围 |
| --- | --- |
| `just test` | Windows，CPU 与 WARP；包含 ECS 固定种子短回归 |
| `just test-hardware` | GPU 与系统 Vulkan 驱动；Vulkan、Intel 适配器、Sampler Feedback、WaveSize(32)、Cluster 完整场景和 VSM |
| `just test-long-fuzz` | `SOMEENGINE_ECS_FUZZ_SEED` 与 `SOMEENGINE_ECS_FUZZ_STEPS`；可提供 `SOMEENGINE_ECS_FUZZ_TRACE` 重放 |
| `just render-perf` | 已构建 DefaultGame 与三个场景；统一测量 1024 小球、Sponza、Amazon Bistro |

硬件类别对应源码中的明确要求：Vulkan 选择系统默认适配器，Intel 和 Sampler Feedback 用例选择具有指定能力的硬件，Dice 寄存器用例要求 WaveSize(32)，Cluster 与 VSM 场景选择硬件适配器。Temporal 与 Cluster 资源加载用例继续通过默认软件入口执行。

硬件和性能工作流由手动操作触发，使用带 `self-hosted`、`Windows`、`X64`、`gpu` 标签的运行器。运行器提供 GPU、Vulkan 驱动、Python、可创建窗口的 Windows 会话以及支持所选 Actions 版本的 runner。性能工作流先调用现有场景下载和构建脚本，再执行 `tools/Measure-Scenes.ps1`；该入口对完整性、三个场景和 60 秒时限进行核对。长模糊工作流支持手动输入种子和操作数，每周日北京时间 04:00 自动执行 10000 次操作的模型对照。

测试以公开操作、返回数据、错误和资源生命周期为判据。合入 #84 时，默认 dotnet 测试入口承接该票的测试规则门禁。

## 失败诊断

`tools/Test.ps1` 将 TRX、控制台日志、崩溃/挂起证据写入 `artifacts/test-results`，并把该目录通过 `SOMEENGINE_TEST_ARTIFACTS` 交给测试。图像测试将失败图像写入此目录。默认工作流失败时上传整个目录，以及现有测试输出目录中的 PNG、BMP、PPM、日志和 `fuzz-failures`，保留 14 天。

`pwsh -NoProfile -File tools/Test-CiFailure.ps1` 临时创建一个故意失败的软件用例，核对非零退出码、TRX 中的测试输出、控制台日志与 PNG，并清理临时工程。手动运行 `CI failure diagnostics` 工作流会执行相同验收，并以失败状态结束，将日志和图像上传到该次运行。

## 软件测试基线（#85）

2026-10-04，基线 `1339869ff7e721ad3d7bcc176d6de96d1f1131e7`，Windows 10.0.26200、SDK 10.0.112、Release：统一构建的 72 个项目全部成功，零警告、零错误。完整默认测试执行 2417 项：2414 通过、3 失败、0 跳过，命令退出码为 1。逐项失败记录如下。

| 软件用例 | 首次完整集合结果 | 单独执行结果 | 跟踪票 |
| --- | --- | --- | --- |
| `GltfVertexInputTests.ImportConvertsSourceLayoutsBeforeCallingTheMaterialEncoder(5, false)` | 第 94 行规范化 UV 观察值为 false | 通过 | [#126](https://github.com/ODtian/SomeEngine.Next/issues/126) |
| 同一方法 `(6, false)` | 同上 | 通过 | [#126](https://github.com/ODtian/SomeEngine.Next/issues/126) |
| 同一方法 `(5, true)` | 同上 | 通过 | [#126](https://github.com/ODtian/SomeEngine.Next/issues/126) |

完整默认命令为 `pwsh -NoProfile -File tools/Test.ps1 -NoBuild`；独立诊断命令为 `dotnet test tests/SomeEngine.Assets.Tests/SomeEngine.Assets.Tests.csproj -c Release --no-build --filter FullyQualifiedName~GltfVertexInputTests`。#126 的观察夹具已按有效顶点范围修正，六组参数各执行两个并行导入，Assets 全集 241 项通过。

## 现有功能覆盖
| 功能 | 现有回归入口与观察结果 |
| --- | --- |
| ECS 基本操作与复制 | `SomeEngine.ECS.Tests` 的实体、组件、查询、缓冲区、共享值、稀疏值和关系测试；复制后独立修改，增删后读取正确 |
| 批次与失败 | `DetachedIndexCloneTests`、`DetachedTableCloneTests`、`StructuralCandidateContextTests`、`TypedRelationshipCommandBufferTests`；失败保持原数据，成功整批生效，之后可以继续合法操作 |
| 并发与调度 | `SchedulerQueuePrimitiveTests` 经公开调度入口核对每个工作项执行一次；调度器、组件访问和拓扑测试保留依赖、异常、冲突访问与关闭回归 |
| 序列化与存储 | `SomeEngine.Serialization.Tests`、`SomeEngine.ECS.Serialization.Tests` 保留读回、损坏与截断输入、校验、容量上限、取消和失败恢复；流式写入通过实际输出与公开世界状态比较 |
| 资源加载 | `ResidentAssetTableTests` 已经改用公开 `AssetLoader`，覆盖重复请求、单个请求取消、重载、失败重试和关闭；资产类型扩展测试继续检查依赖释放 |
| 图形后端 | D3D12、Vulkan 测试保留上传、执行、读回、非法对象和资源生命周期；Vulkan 使用公共工厂和正常产品构建，D3D12 私有反射辅助层已移除 |
| 渲染图与提取 | `StableFrameAllocationTests` 保留跨帧执行与图编辑，移除分配预算；提取测试保留结果、失败回滚和恢复 |
| 代码生成 | 部分既有消费者用例编译并执行生成 API；其余用例仅验证输入声明的编译诊断，不算作生成 API 执行覆盖；源码写法及文件名断言已移除 |
| 原生 AOT | `just ecs-aot <rid>` 与 `just ecs-aot-matrix <rids>` 实际发布、启动原生程序；序列化冒烟读取并比对普通块及 Brotli 块内容 |
| 随机回归 | 独立参考模型比较公开实体、组件、关系和查询结果；保留种子、失败操作与重放，移除私有版本和内部计数判据 |

## 独立入口

- `just d3d12-device-loss`：独立测试进程，通过公开原生访问能力移除真实 D3D12 设备，再检查公开操作的错误行为。
- `just d3d12-presentation`：独立呈现测试进程。
- 两个入口分别写入 `artifacts/test-results` 下的 TRX 文件；默认测试仍排除这两类隔离用例。
- `just ecs-fuzz-campaign <seed> <steps>` 和 `just ecs-fuzz-replay <trace>`：可选长测与重放。未配置时明确跳过。
- 两套基准工具测试按各自 `.csproj` 单独选择，退出默认功能集合。基准测量只报告数值，不以性能预算决定成败。

## 本轮不补的新测试与覆盖缺口

依照最新指示，本轮不补以下测试。清理内部检查不等于已经补齐对应功能覆盖：

- 稀疏映射复制与自重叠：既有命令完成用例没有核对复制后的数据，不算作映射结果验证。
- 仅检查编译诊断的泛型及其他生成器用例，不证明生成的调度入口可调用或执行正确。
- ECS 当前并发用例检查写入结束后的值；并发读取看不到半批更新的结果测试仍缺，本轮不补。
- Vulkan 呈现测试只覆盖当前拒绝呈现能力请求的行为，非 Windows 明确跳过；不代表成功呈现覆盖。
- Vulkan 原生提交、内存分配、等待和管线创建中的指定 OOM 或设备丢失结果，以及指定时刻的异步缓存释放竞争。
- D3D12 原生等待失败、呈现重配置/信号失败、释放期间抛错，以及特定回收列表交错。真实设备移除仍有独立回归。
- 原来只比对 Shader 源码的照明表达式并不能证明像素结果。移除这些断言不代表已经补齐背光和灯光筛选的像素回归。

需要外部可复现条件时，应直接测量公开操作的错误、状态和读回结果，不新增内部故障开关或反射入口。本文没有声称这些覆盖缺口已解决，也没有声称本次修改已通过执行验证。
