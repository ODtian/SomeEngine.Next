# Harness Input

## Hard Requirements

- 建立版本化 capability-continuity manifest。每项使用稳定 capability id，并记录：ZIP baseline level、证据路径/符号、当前 public/Null/D3D12/RG/test 映射、required lanes、test ids、accepted replacement/removal decision。未映射的 ZIP 项、无依据降级或文档高于证据等级必须失败。
- capability level 至少区分 `metadata`、`public-contract`、`compiler-lowering`、`null-execution`、`native-execution`、`renderer-consumer`。专项能力 DXR、mesh shader、VRS、tiled/sparse、sampler feedback、work graphs必须逐项有真实等级和缺失 API 语义清单。
- 把四个产品项目和四个相关测试项目纳入 declared boundary 与单一 product-test runner：Graphics、Graphics.Null、Graphics.Direct3D12、RenderGraph；Graphics.Tests、Graphics.Direct3D12.Tests、RenderGraph.Tests、Assets.Tests。
- required test id 必须可发现且非零；required Windows/WARP lane 禁止以 `if (!OperatingSystem.IsWindows()) return`、动态 skip 或零发现伪装通过。
- 为 mandatory core 建立 API-shape/behavior tests：capability snapshot、traditional binding、optional bindless fail-close、resource/heap/format/limits、queue/submission/lifetime、pipeline readiness/cache、query/calibration、swapchain/present/resize、diagnostics/device loss。
- indirect 必须覆盖三类命令、CPU/GPU count、多命令、offset/stride/max-count、同/异 buffer range、wrong usage/state/queue/scope/bounds，以及 GPU producer -> indirect consumer 的 RG hazard。D3D12/WARP 必须校验真实 UAV或像素输出。
- descriptor harness 必须以超过旧 4096 resource / 256 sampler 容量的负载验证 heap grow/switch、active binding replay、旧 heap retirement 与多 command context并发；禁止只把容量调大。
- PassParameters/ShaderParameters harness 必须覆盖：现有 asset reflection 唯一真相、一次 pairing、compile-before-execute、生成 access/view/constant/descriptor glue、logical ID+view原子值、无热路径 reflection/string lookup、cache key/invalidations、错误字段/kind/count/access fail-close。
- RG continuity tests 必须覆盖 temporal/history ring/reset/resize/in-flight、export作为 culling root及 completion 后发布、capture JSON/DOT deterministic schema与 replay、cache on/off 和 hit/miss结果一致。
- D3D12 query 必须使用真实 query heap、queue timestamp frequency和 clock calibration；禁止 `Environment.TickCount64` 等伪 GPU calibration。
- swapchain tests 必须区分 WARP/native smoke 与真实 HWND/display hardware lane，并覆盖 acquire/present state machine、resize、vsync/tearing、occlusion和 backbuffer lifetime。
- benchmark infrastructure 存在且可执行，输出版本化 JSON 与 CPU/OS/adapter/driver/build metadata。至少有 compiler/cache、RHI command/descriptor/resource、10k-frame lightweight soak 场景；性能阈值先作为 warning，不得伪造目标值。
- 修复 clean-checkout 闭包：solution/tool 不得依赖未跟踪 `dxc.exe`；依赖策略必须由 tracked/categorized inputs满足。
- 文档声明 gate：`implemented/supported/native` 等声明不得高于 manifest level；`metadata-only`、`partial`、`absent` 必须明确。
- 当前更严格的 generational handles、cross-device/stale rejection、exact requirements/copy footprints、deferred retirement、range/subresource hazards、transparent cache与shader effect admission必须有防回退测试。

## Review Requirements

- 审查迁移是否以当前架构为目标做语义移植，而不是把 ZIP monolith、CPU mirror、catch/rethrow噪音、fake caps 或 Null delegation搬回。
- 审查所有 ZIP capability 都有 ledger 映射；accepted replacement 必须保持或提高可观察语义，不能以“当前消费者未使用”删除。
- 审查 advanced feature 记录没有把 partial token/API夸大成 execution，也没有把旧部分实现误写为全局不存在。
- 审查 public API/backend-neutral 边界，不泄漏 Vortice/DXGI/D3D12 类型；traditional path不依赖 bindless。
- 审查 immediate RG 与 transparent cache：cache 关闭只影响性能；不得恢复第二套 retained compiler。
- 审查 ShaderParameters 未绑定具体 shader/路径/entry，且没有绕过现有 asset database/importer/runtime shader/material链。
- 审查 descriptor rollover 以 active binding state/CPU descriptor source为真相，切 heap后正确重新 materialize和重绑。
- 审查 mandatory core 无 stub、兼容壳、silent fallback、magic fixed-capacity failure 或未处理的 backend `NotSupportedException`。
- 审查所有新 public type/method naming research 证据和 blacklist；原始/标准术语复用也需说明来源。
- 审查改动不包含 ECS、Job、RenderWorld 或旧仓库内容。
- 审查 wiki 只保留可复用结论，raw Q&A 只在 grill artifact。

## Warning Candidates

- compiler transparent-cache、alias、merge 的收益与具体 p50/p95/p99。
- descriptor churn、resource/view pool 命中率、queue overlap和CPU/GPU frame timing。
- 多厂商 hardware matrix 中当前机器不可获得的 adapter lanes；不得把缺 artifact 的硬件声明升为 native support。
- Cluster renderer 代表性 workload 的性能阈值；本 run 只要求 benchmark/soak infrastructure和合成 core evidence。
- optional bindless硬件成功路径在本机无对应 capability 时的缺失 artifact；capability false/fail-close仍是 hard。

## Out of Scope

- Cluster renderer产品接入。
- Vulkan/Metal/WebGPU backend实现。
- DXR、mesh shader、VRS、tiled/sparse、sampler feedback、work graphs的 native execution；只做真实性账本和缺失 API语义。
- D3D12 mixed-state indirect command signatures。
- 与 ECS/Job/RenderWorld并行工作相关的 build/test修复。
- 未经真实 workload数据接受具体性能阈值。

## Re-Grill Triggers

- capability manifest证明某项被归为 out-of-scope 的能力其实属于已接受 mandatory core或原始 native baseline。
- mandatory core 无法在 FL12_0 + SM6.2 traditional-binding路径表达。
- 需要以 bindless、SM6.6、Tier3作为准入条件。
- 需要让公开 graph变成 retained语义或让 cache改变行为。
- 需要引入未确认 heuristic、magic capacity或硬件型号白名单。
- 需要修改旧仓库或用户的 ECS/Job并行改动。
