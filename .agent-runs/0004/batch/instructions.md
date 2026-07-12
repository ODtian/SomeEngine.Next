# Batch Instructions

## Objective

以 `c0ac382e` 的 `SomeEngine.Graphics`、Null、Direct3D12 和 immediate `SomeEngine.RenderGraph` 为唯一产品基座，把 capability-continuity manifest 中列为本 run mandatory 的原始 RHI/RG 能力完整迁回。迁移必须同时闭合 backend-neutral public contract、严格 Null oracle、真实 D3D12 lowering、RenderGraph dependency/lifetime integration、shader asset/source-generated binding、可执行验证、benchmark/soak、clean-checkout 和文档真相；不得通过删除 API、降低 manifest 等级、兼容空壳或静默 fallback 使门禁变绿。

## Inputs

- `.agent-runs/0004/intent.md`
- `.agent-runs/0004/harness.md`
- `harness/capabilities/graphics-rendergraph-capabilities.v1.json`
- `harness/capabilities/graphics-rendergraph-public-api-inventory.v1.json`：原 ZIP `IDevice` 130 个 method declaration 与 `RenderGraph.Core` 100 个 public type declaration 的逐符号账本
- `RenderGraph-opt-refactor-checkpoint-d3d12-native-descriptor-hardening-verified-not-complete.zip`，SHA-256 `F2C1FC049134DB57C8B6BF038E3D0270ECC5FF7AB1A58E72D8D411FF81E53987`
- 当前 Graphics/Null/Direct3D12/RenderGraph/Assets/Render shader contract 与现有 tests
- `batch/review-targets/*.md`

## Review Targets

- `advanced-features-are-reported-truthfully`
- `capability-continuity-has-no-silent-loss`
- `clean-checkout-benchmark-and-scope-are-real`
- `current-rhi-rg-semantics-are-preserved`
- `d3d12-core-is-native-and-backend-neutral`
- `descriptor-rollover-replays-active-state`
- `immediate-rg-restores-temporal-export-and-capture`
- `naming-research-and-breaking-migration-are-complete`
- `shader-parameter-generation-uses-asset-truth`

## Classification

- 本轮已完成：仅在 capability ledger、对应产品实现、可执行验证和 review result 同时闭合后使用。
- 本轮未完成：任何 mandatory capability、required lane、review target 或 hard gate 尚未闭合时使用。
- 不属于本轮：ECS、Job、RenderWorld、Cluster renderer 产品接入和旧仓库改动。

## Work Items

- 保留并扩展当前 generational handles、device domain、三队列 completion/retirement、native allocation requirements/copy footprints、CPU descriptor page pool、exact range/subresource hazards、immediate recording、transparent compiled-plan cache 和 ShaderAsset effect admission；禁止以 ZIP 整包覆盖这些语义。
- 使 device capability/adapter/format/limit snapshot fail-closed，并把 FL12_0 + SM6.2 + traditional binding 固定为 production baseline；bindless 仅作为可选 profile。
- 完成 typed `DrawIndirect`、`DrawIndexedIndirect`、`DispatchIndirect`，包括 CPU/GPU count、多命令、argument/count buffer offset、stride、max count、同/异 range、Null validation、D3D12 command-signature cache、RG hazards 和 WARP output validation。
- 完成 timestamp、occlusion、pipeline-statistics query pool、resolve、queue timestamp frequency 和真实 clock calibration；不得保留 checkpoint 的 CPU tick 伪校准。
- 完成 swapchain acquire/backbuffer/present/resize/vsync/tearing/HDR-color-space/occlusion/device-removed contract；Null 状态机、WARP/native smoke 与真实 HWND/display lane 明确分离。
- 完成 raster/compute pipeline readiness/failure/cache identity/persistent cache invalidation，以及 DRED/device-loss/InfoQueue 结构化诊断。
- 让 D3D12 shader-visible descriptor heap 在超过旧 4096 resource / 256 sampler 容量时切换 heap、从 CPU descriptor truth 重放 active bindings、正确退休旧 heap，并覆盖多个并发 command context。
- 把 temporal/history ring、reset/resize generation、frames-in-flight ownership、export/extraction liveness root、GPU completion 后发布、deterministic capture JSON/DOT/replay 迁入 immediate graph；retained template/variant 的可观察语义映射到 canonical recording + transparent cache，不恢复第二套 compiler。
- 完成 `PassParameters` / `ShaderParameters` source-generated access/view/packing/descriptor glue；现有 ShaderAsset/MaterialPass reflection 是唯一 shader truth，pairing 在 compile 前完成，resource ID 与完整 view 是单个值，Execute 热路径没有 reflection 或字段字符串查找。
- 把 Graphics、Graphics.Null、Graphics.Direct3D12、RenderGraph 及 Graphics.Tests、Graphics.Direct3D12.Tests、RenderGraph.Tests、Assets.Tests 接入 declared boundary 与单一 product-test runner；required test id 必须被发现，Windows WARP lane 不得静默 skip。
- 修复 AssetCook 对未跟踪 `dxc.exe` 的 clean-checkout 依赖；建立可执行、版本化 JSON 的 compiler/cache、RHI command/descriptor/resource benchmark 和 10k-frame lightweight soak，记录 CPU/OS/adapter/driver/build metadata。
- 逐项更新 capability manifest 的 current mappings、levels、lanes、test IDs 和证据。DXR、mesh shader、VRS、tiled/sparse resources、sampler feedback、work graphs 只记录本 run 的真实等级与缺失 API，不伪造 native execution。
- 不得删除、合并或抽样 public API inventory 来使账本变绿；每个 ZIP declaration 必须保持唯一 capability ID 与 disposition。新增 mandatory 红灯包括 texture-to-texture copy、explicit clears、scoped host mapping、object names/point markers，以及 executable capture replay。command pools/secondary buffers、raw queue waits、tokenized bind writers、retained template/variant/instance pools只能用 ledger 中已接受的 stronger replacement 对齐。
- 结构化 capture/replay 的等级上限是 `compiler-lowering`；只有真正重建资源、记录并提交捕获命令且验证可观察输出，才关闭独立的 `rg.executable-capture-replay` `null-execution` 红灯。
- 修正文档/wiki，使 `implemented`、`supported`、`native` 等声明不高于 executable evidence；只沉淀可复用架构结论，raw grill 问答留在 run artifact。

## Success Criteria

- manifest 中每个原始 ZIP capability 都有唯一稳定 ID、aliases、baseline evidence、current mapping、真实 level、required lane、test ID 或明确 accepted replacement；230 条原 public declaration 与 ZIP 机械提取集合完全相等；mandatory capability 达到本 run 要求等级且没有未批准降级。
- mandatory core 形成 public API → Null → D3D12 → RG → executable test 的闭环；Windows 上 WARP 真实执行 indirect/query/descriptor/core RG 输出验证，required lane 无零发现或 silent skip。
- temporal/history、export/extraction、capture/replay 和 transparent-cache replacement 的行为测试通过；cache 开关、hit/miss 不改变输出。
- ShaderParameters/PassParameters 生成链、asset reflection pairing 和热路径禁反射测试通过。
- descriptor rollover/replay 压力测试、clean-checkout dependency gate、benchmark/soak infrastructure gate通过。
- 现有 Graphics/RG correctness regression tests 继续通过；无 ECS、Job、RenderWorld 或旧仓库改动进入本 run diff。
- 所有 review target 均有通过证据，single harness hard gate 返回 `PASS`；warning 性能结果被记录但不伪造阈值。

## Stop Conditions

- manifest 证据证明某项 out-of-scope advanced feature 实际属于已接受 mandatory native baseline。
- mandatory core 无法在 FL12_0 + SM6.2 + traditional binding 表达，或实现被迫把 bindless/SM6.6/Tier3 提升为设备准入条件。
- 实现需要公开 retained graph 语义、第二套 compiler、cache 影响正确性、未确认 heuristic/magic threshold/设备白名单。
- 完成必须修改旧仓库或用户的 ECS/Job/RenderWorld 并行工作。
- ZIP 证据与 manifest baseline 冲突并会改变已接受范围；使用 `NEEDS_GRILL:` 说明具体 capability、证据和范围影响。
