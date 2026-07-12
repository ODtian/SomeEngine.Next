# Intent

## Goal

在 `F:\SomeEngine.Next` 中，以已提交的 `c0ac382e` 为可靠基座，把原始 checkpoint ZIP 中真实存在的 RHI/RenderGraph 生产核心能力迁回并补完，形成可供后续 Cluster renderer 直接消费的 backend-neutral `SomeEngine.Graphics` + immediate `SomeEngine.RenderGraph`。完成是端到端的：公共契约、Null oracle、D3D12 native lowering、RG integration、shader asset/SG binding、自动 harness、真实输出验证、benchmark/soak 和文档必须同时闭合。

## In Scope

- 建立 checked-in capability continuity ledger，逐项映射原始 ZIP 的 public API、Null、D3D12、RG、test 和 consumer baseline；任何删除、降级或替代都必须有明确证据。
- 保留当前实现中更可靠的 generational handle/device domain、多队列 completion/retirement、native requirements/copy footprints、descriptor CPU page pool、严格 Null validation、即时 RG、精确 range/subresource dependency、透明 compilation cache、shader effect asset contract。
- 完成 3A raster/compute production core：
  - typed indirect draw、indexed draw、dispatch，CPU/GPU count、多命令、offset/stride/max-count；
  - 传统 bind group 路径和 descriptor arrays；D3D12 shader-visible heap 容量不足时切换 heap并复制/replay当前 active bindings，不再以固定 4096/256 容量抛出作为终态；
  - bindless 作为可选 capability/profile，不能成为设备准入条件，也不能由静默 fallback 假装支持；
  - graphics/compute/copy queue、跨队列同步、submission、frames-in-flight 和 deferred retirement；
  - committed/placed/alias resource、upload/readback、copy/resolve、format/usage/allocation requirements、memory budget/residency telemetry；
  - timestamp、occlusion、pipeline statistics query 与真实 timestamp frequency/clock calibration；
  -完整 raster/compute pipeline、readiness/failure、cache identity、持久 pipeline cache与失效；
  - swapchain、acquire、backbuffer、present、resize、vsync/tearing、HDR/color-space、occlusion/device-removed 状态；
  - capability、format、limit、adapter/driver snapshot、InfoQueue、DRED/device-loss diagnostics。
- 迁回 checkpoint 中真实存在的 RG 语义：temporal/history、extraction/export、persistent ownership handoff、capture/replay、JSON/DOT 与结构诊断；旧 retained template/variant 能力必须映射到当前即时录制 + 透明 cache 语义，不得恢复第二套不一致 compiler 或让 cache 改变行为。
- 统一采用 `PassParameters / ShaderParameters`：参数形状不绑定 shader；现有 ShaderAsset/MaterialPass/pipeline 是 reflection 唯一来源；binding contract 在 RG 编译前确定；SG 生成 access、view、packing、descriptor/bind glue；热路径无反射/字段字符串查找；复用现有 `TextureId / BufferId`，资源 ID 与完整 view 描述组成单个值。
- 把 Graphics/Null/D3D12/RG/Assets 相关项目和 tests 正式接入单一 harness；Windows WARP lane 必须真实执行且不允许静默 skip。
- 建立可执行 benchmark/soak infrastructure；固定结果格式和机器/adapter metadata 是 hard requirement，具体性能阈值在取得 Cluster 代表性负载前保持 warning。
- 修复 checkpoint commit 暴露的 clean-checkout 闭包问题，包括 AssetCook 对未跟踪 `dxc.exe` 的依赖和文档对当前能力的虚假声明。

## Out of Scope

- 本 run 不接入 `SomeEngine.Render.Cluster` renderer；它在 RHI/RG core 通过后开始。
- 不修改旧仓库 `F:\SomeEngine`；迁移真相来源是 Next 中的原始 ZIP、既有 grill 与当前代码，不把旧仓库误当目标。
- 不实现 Vulkan、Metal 或 WebGPU backend；公共语义仍必须 backend-neutral。
- 不在本 run 端到端实现 DXR、mesh shader、VRS、tiled/sparse resources、sampler feedback 或 work graphs；它们必须按 `metadata / API / compiler / Null execution / native execution / consumer` 六级如实登记，不能伪造空壳 API或把文档/token当完成实现。
- 不把 D3D12 mixed command signature（每条 indirect record 修改 VBV/IBV/root constants/descriptors）纳入通用 indirect core；作为缺口登记。
- 不覆盖或提交 ECS、Job、RenderWorld 等并行用户改动。
- 不保留旧 API compatibility shell、`[Obsolete]` 双入口、Null delegation、CPU shadow 或运行时隐式 fallback。

## Must Hold

- D3D12 production baseline 是 Feature Level 12_0 + Shader Model 6.2；传统绑定必备。SM 6.6、direct heap indexing、Resource Binding Tier 3 和 bindless 都是可选能力。
- 不支持 bindless 的设备仍能执行完整传统 graphics/compute/indirect core；optional capability 的错误使用必须 fail-close。
- 原始 ZIP 中真实 native-execution 能力不得降成 metadata/API-only；原始 ZIP 本来不存在或不完整的能力不得被文档夸大。
- 所有 core capability 都必须形成 `public API -> Null -> D3D12 -> RG -> executable test` 闭环；required backend 不允许 `NotSupportedException`、stub 或只断言“不抛异常”。
- `SomeEngine.Graphics` 和 RG 不暴露 Vortice/DXGI/D3D12 native types。
- immediate graph recording 是公开正确性语义；transparent cache 可关闭且不能改变结果。
- shader/entry/reflection 只来自现有 asset pipeline；禁止 path/entry attribute、generated shader marker 或旁路 asset truth。
- breaking redesign 后仓库内消费者一次性迁移；不交半套 handoff。
- 不引入未报告并获确认的 heuristic、magic threshold 或设备型号白名单。现有 transparent cache LRU/alias/merge policy 不在本 run 偷换。
- 新公共类和方法名称采用前完成 GitHub naming research；优先复用已有 checkpoint、D3D12/Vulkan/Metal/WebGPU 的成熟词汇，禁止 `*Plan`、`*Run`、非入口点 `*Program`。

## Must Not Happen

- 再次用“清理假实现”为理由删除整个 capability family。
- 把高级 feature token、文档草案、接口 cast 成功或 capability default=true 记成实现完成。
- 把 WARP unsupported skip 记成 native pass，或让 required WARP tests 在 Windows 上零发现。
- 用整包覆盖回退当前更严格的 resource/lifetime/cache/shader contract 架构。
- 用 RG 私有逻辑掩盖 RHI core API 缺失，或在 Execute 热路径首次反射、匹配字段、创建不必要的 native view。
- 修改或提交与本 run 无关的脏工作树内容。

## Acceptance in User Terms

- 当前 checkpoint 先有可回退 commit；迁移结果另有独立 commit。
- indirect 不只是有方法：GPU 写 args/count 后，Null 与 D3D12/WARP 真正执行 draw、indexed draw、dispatch，并校验像素/UAV readback。
- 原始 ZIP 的每个 RHI/RG capability 都能在 ledger 中找到当前映射、证据、测试与等级；缺失 API 逐项记录。
- production core 在标准 harness 中默认构建与运行；WARP/debug/InfoQueue lane 无静默 skip。
- temporal/history、export/extraction、capture/diagnostics 等原始 RG 语义恢复到当前即时架构，且透明 cache 命中/关闭结果一致。
- PassParameters/ShaderParameters、asset reflection pairing 和生成绑定链端到端可用；旧手工 access/bind 主路径退出公共 API。
- descriptor 压力超过旧固定容量时 grow/switch/replay 正常，不因容量魔数直接失败。
- benchmark runner、机器元数据、JSON artifact 和 soak 可执行；真实性能阈值在 Cluster workload 接入后再冻结。
- docs/wiki 只声称测试证明的 capability level；专项能力现状有证据，不再被误写为“真的没有”或“已经完成”。

## Wiki Impact Candidates

- RHI capability continuity 与 optional profile 等级模型。
- backend-neutral binding、descriptor materialization 与 D3D12 heap rollover/replay。
- immediate RenderGraph、transparent compiled-plan cache 与 temporal/export ownership。
- PassParameters/ShaderParameters、ShaderAsset reflection 与 generated binding contract。
- D3D12 production baseline、WARP/hardware lanes 与 capability evidence rules。

## Re-Grill Triggers

- 需要把 DXR、mesh shader、VRS、sparse/tiled、sampler feedback 或 work graphs 的 native execution加入本 run。
- 需要把 bindless 改为 mandatory device baseline，或提高 FL12_0/SM6.2 基线。
- 需要恢复 retained graph 作为公开语义，或让 cache 影响正确性。
- 需要引入新的 heuristic、magic capacity/threshold 或设备型号白名单。
- 发现原始 ZIP capability baseline 与 ledger 证据冲突，导致 required core 范围发生语义变化。
- 必须修改 `F:\SomeEngine` 或与 ECS/Job 并行工作才能完成。
