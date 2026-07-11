# C# / .NET 10 3A Render Graph 技术设计与一期全量实施任务

> **Status: superseded research input, not an implementation baseline.** This imported v3 draft conflicts with the [accepted Render Graph architecture](../wiki/architecture/Render-Graph.md), [ADR-0005](adr/0005-ue-style-immediate-render-graph.md), and [ADR-0006](adr/0006-transparent-render-graph-compilation-cache.md), notably through public graph templates/instances/variants and their user-visible variant cache, public write-version semantics, graph-owned temporal/history migration, graph-owned bindless/residency/pipeline policy, and compiler-injected semantic work. Preserve it only for evidence and algorithm review; its cache implementation is not the accepted transparent cache design.

**调研对象**：Bevy、Granite（Themaister）、AMD Render Pipeline Shaders SDK、Frostbite FrameGraph、Unreal Engine 5 Render Dependency Graph
**目标运行时**：.NET 10 / C# 14
**真实后端**：Vulkan 1.3、Direct3D 12；另含 Null 验证后端
**异步渲染基线**：Bevy RenderWorld 所有权移交
**子资源基线**：UE5 RDG 的 mip / array slice / plane 状态模型
**跨帧基线**：AMD RPS temporal resource + Granite history
**文档版本**：2.0（一期全量范围修订）
**修订日期**：2026-06-21

---

## 文档定位

本文是可直接进入工程实现和评审的技术基线，不是概念介绍。它定义公开 API、内部数据结构、依赖与版本算法、任意 mip/layer/plane 子资源状态、buffer 区间状态、跨帧 temporal family、自动迁移、多队列调度、瞬态内存、驻留与 bindless、Raster 合并、RTAS/VRS/sparse/GPU-driven、RenderWorld 流水、Vulkan/D3D12 映射、验证、捕获、随机测试、性能预算及一期任务依赖。

**本文只有一个交付范围。** T01～T20 是可以并行分配、具有先后依赖的工程任务，不是产品阶段，也不代表任何能力被推迟。所有任务及其验收项共同构成一期完成定义。

配套文件：

- `RenderGraph.Api.cs`：公开契约草案；
- `render_graph_api_name_evidence.json`：每个 public 标识符的 GitHub 搜索与源码证据；
- `render_graph_name_audit.json`：禁止后缀自动审计；
- `render_graph_task_matrix.json`：需求—任务—测试追踪；
- `CHANGELOG.md`：相对 1.0 的范围修订。

## 目录

0. 结论与纠偏
1. 一期范围与完成定义
2. 参考对象选择
3. 总体架构
4. 每帧生命周期与线程边界
5. Bevy 式 RenderWorld
6. 公开 API 与命名约束
7. 资源、视图、版本与物理对象
8. 任意 mip/layer/plane 子资源跟踪
9. Buffer 区间跟踪
10. 跨帧 temporal family 与自动迁移
11. 图编译处理链
12. 依赖、裁剪、排序与缓存
13. 多队列、异步计算与屏障
14. 瞬态内存、驻留、描述符与 bindless
15. Raster、Resolve、Mip 与原生 Pass 合并
16. 3A 能力完整性
17. Vulkan 1.3 / D3D12 / Null 后端
18. C# / .NET 10 工程实现
19. 验证、诊断、捕获与故障恢复
20. 一期任务清单与依赖关系
21. 测试与一致性矩阵
22. 性能预算
23. 仓库结构与模块边界
24. 风险与决策记录
25. 需求追踪与交付门禁
26. API 契约附录
27. 参考资料

# 0. 结论与纠偏

前一版把“任意 mip/layer 子资源跟踪”和“跨帧资源自动迁移”写成非目标，这是错误的范围切割。面向 3A 游戏的 Render Graph，如果仍以整纹理状态和手工历史导入作为正式模型，会在以下场景直接失去正确性或产生不可接受的过度同步：

- 分级 bloom、Hi-Z、SSR、radiance cache、mip-chain 生成；
- cubemap 六面并行更新、array shadow atlas 的局部 layer 更新；
- depth/stencil 分 plane 访问；
- TAA、TSR、DLSS/FSR/XeSS、motion history、exposure history、reservoir sampling；
- 动态分辨率、窗口 resize、相机切换、设备恢复；
- async compute 读写不同 mip 或不同历史槽；
- 大型 GPU-driven 聚合 buffer 的局部区间并发。

因此 2.0 采用以下组合，而不是让某一个参考框架垄断全部设计：

| 问题 | 主参考 | 辅助参考 | 目标方案 |
|---|---|---|---|
| CPU 世界与渲染世界异步 | Bevy RenderWorld | Frostbite Setup/Execute 分离 | 容量 1 双向所有权移交；RenderWorld 单写者 |
| texture 子资源状态 | UE5 RDG | RPS resource view | mip × array layer × plane 一等状态；统一快路径与展开路径 |
| 跨帧资源 | AMD RPS temporal resources | Granite render-target history、UE extraction | temporal family 自动槽解析、跨帧 producer、generation 与迁移 Pass |
| 全帧编译与资源调度 | AMD RPS | Frostbite、Granite | 编译器式处理链，批次、内存、屏障与诊断同源 |
| 即时 Builder 与验证 | UE5 RDG | Unity RenderGraph API 名称 | 普通 C# 每帧构图，执行回调内解析原生资源 |
| Raster 优化 | Granite、UE5 RDG | Vulkan dynamic rendering / render pass | load/store 推导、resolve、mip、subpass/fusion、tile memory |

本次实现的基本结论是：

1. **资源与视图分离。** `TextureHandle` 表示逻辑资源版本，`TextureViewDesc` 表示实际访问范围和格式；所有依赖来自视图范围，而不是资源名。
2. **局部写入是覆盖映射，不是整资源版本号加一。** 新版本只替换覆盖范围的 producer；未覆盖范围继承旧版本。
3. **跨帧历史是图资源类别。** 业务代码只声明 `HistoryCount` 与 `HistoryIndex`；物理槽、最终状态、完成值、迁移和销毁均由 RenderWorld 中的 temporal registry 管理。
4. **迁移是图内工作。** resize、格式变化、sample count 变化需要 copy、resample、resolve、convert 或 clear 时，编译器注入可见 Pass，参与裁剪、同步、计时和捕获。
5. **所有 3A 能力进入同一期。** 平台不支持时允许 capability fallback，但数据模型、验证、诊断和至少一个可执行路径必须完成，不能以“预留”为完成。

# 1. 一期范围与完成定义

## 1.1 功能范围

一期必须实现以下完整闭环：

- 每帧即时构图、虚拟资源、PassData、执行回调；
- texture 任意 mip / array layer / cube face / plane 范围；
- buffer byte range 与结构化 element range；
- 版本依赖、部分写、discard、read-modify-write、WAR/WAW/RAW；
- 根识别、反向裁剪、确定性拓扑排序、环诊断；
- Graphics / Compute / Copy，多队列 timeline、queue ownership 和回落；
- temporal family、N 级历史、in-flight 槽保护、跨帧 producer、最终子资源状态；
- resize / dynamic resolution / format / sample count 变化的自动迁移；
- 瞬态 heap、别名、预算、驻留、延迟销毁；
- descriptor/view cache、transient descriptor arena、稳定 bindless index；
- attachment load/store、MSAA resolve、depth resolve、自动 mip、input attachment；
- native render-pass fusion / tile memory 合并；
- GPU-driven indirect、count buffer、predication；
- ray tracing acceleration structure build/update/compaction 和 scratch hazard；
- sparse/reserved resource、virtual texture page table 与 residency fence；
- VRS、foveated rasterization、multiview / view mask；
- upload、readback、timestamp/occlusion/statistics query；
- 外部资源和第三方 SDK 互操作；
- Bevy 式 RenderWorld 异步 CPU 流水；
- Null、Vulkan 1.3、D3D12 三后端的一致性测试；
- DOT/JSON/timeline/memory map、capture/replay、GPU crash breadcrumbs；
- 随机图、属性测试、后端 validation、性能门禁。

## 1.2 不允许以“预留”代替实现

以下状态不算完成：

- 类型里有 `Range` 字段，但 Builder 只接受全范围；
- `HistoryCount` 存在，但仍由功能代码每帧手工 import；
- 有 `QueueType.Compute`，但没有跨队列 wait/signal 和 ownership；
- 有 `Aliasable` 标志，但别名判断仍使用线性 Pass 序号；
- 有 Vulkan 路径，D3D12 只编译不运行 conformance；
- 有 JSON 导出，但不能解释迁移、裁剪、异步或别名原因；
- 有功能 enum，但没有 validation、fallback 和测试。

## 1.3 完成定义

一期完成必须同时满足：

| 类别 | 完成条件 |
|---|---|
| API | 所有 public 标识符有 GitHub 证据；禁用后缀 0 违规；二进制兼容基线已生成 |
| 正确性 | 依赖、范围、状态、别名、跨帧等待均通过参考模型和随机测试 |
| 后端 | Vulkan validation 与 D3D12 debug layer 0 错误；Null/Vulkan/D3D12 输出一致 |
| RenderWorld | 无双重所有权、无关闭死锁、线程亲和析构正确、Simulation N+1 与 Render N 可重叠 |
| 3A 场景 | deferred + async SSAO + TAA/TSR history + GPU-driven + RTAS + VRS + sparse streaming 样例通过 |
| 诊断 | 能回答“为何有此边/屏障/等待/迁移/裁剪/未别名/未异步” |
| 性能 | 稳态 0 B managed allocation；编译、记录、内存峰值达到第 22 章预算 |
| 发布 | 文档、API、证据、任务追踪、测试结果、基准结果打包一致 |

# 2. 参考对象选择

## 2.1 Bevy：只参考 RenderWorld 所有权，不照搬 wgpu 图语义

Bevy 当前 pipelined rendering 实现使用两条容量为 1 的通道传递整个 RenderApp/SubApp。主线程取得渲染世界后执行 extract，再把所有权发给渲染线程；渲染线程 update 后归还。该模型的价值是可证明的单写者、天然背压和关闭时世界回收，而不是某个具体 ECS API。[B1][B2]

目标实现采用：

- `Channel<RenderWorld>` 双向各一条；
- `BoundedChannelOptions(1)`、`FullMode=Wait`；
- 主线程只在持有世界时 Extract；
- 渲染线程只在持有世界时 Prepare/Queue/Build/Execute/Cleanup；
- RenderWorld 持有 device、图缓存、temporal registry、descriptor allocator 和 deferred release queue；
- 同步模式复用相同 schedule，不维护第二套行为。

## 2.2 UE5 RDG：子资源状态主参考

UE5 的 `FRDGTextureSubresourceRange` 明确包含 mip、array slice 和 plane 的起点与数量；`FRDGSubresourceState` 保存单个子资源的 access、first/last pass 和 pipeline；`FRDGTexture` 维护各子资源当前状态与最后 producer。[U2][U3][U4]

目标实现直接吸收其结构性思想：

- 子资源范围是一等访问数据；
- 同一 texture 的不同 mip/layer/plane 可以拥有不同 producer、状态、queue 和寿命；
- 纹理初始走 uniform fast path，首次局部访问再展开；
- barrier 输出前重新合并相邻等价范围；
- depth 与 stencil 可独立跟踪。

不照搬 UE 宏系统。C# 采用显式 Builder + source generator，保持 NativeAOT 与 IDE 可读性。

## 2.3 AMD RPS：temporal 与编译器主参考

RPS 把 temporal resource 建模为多个物理 slice，并通过 frame index、temporal layer 和完成帧索引解析当前/历史资源；resource view 同时携带 subresource range；运行时编译器输出 queue batch、wait/signal、barrier 和内存布局。[R1][R2][R3]

目标实现采用：

- temporal family = 稳定逻辑 id + 描述 generation + 物理槽集合；
- `HistoryIndex=0` 表示当前帧，`1..N` 表示前 N 帧；
- 每槽记录 per-subresource final state、queue、last writer 和 completion value；
- 编译时创建跨帧虚拟 producer 边；
- 槽复用以 GPU completion 为条件，不以 CPU 已进入下一帧为条件；
- 迁移 Pass 进入同一图编译处理链。

## 2.4 Granite：实际优化行为参考

Granite 的公开 Render Graph 包含自动 layout/load-store、async compute、early signal/late wait、资源别名、subpass merge、自动 mipmap、transient attachment、render target history、条件 Pass 和自动 MSAA resolve。[G1][G2]

目标实现把这些能力全部纳入，不把 render-pass fusion、自动 mip 或 history 视为边缘扩展。Granite 用于验证“功能组合是否在实际引擎中成立”，而 UE/RPS 分别负责更精确的状态与 temporal 语义。

## 2.5 Frostbite：全帧生命周期与异步内存警告

Frostbite 的公开设计强调 Setup/Compile/Execute 三段结构、写版本、反向裁剪、瞬态资源生命周期与 async compute 对寿命的扩张。[F1][F2] 目标实现吸收其全帧思考方式，但不会把项目实施拆成多个交付阶段。

## 2.6 参考矩阵

| 设计点 | Bevy | Granite | RPS | Frostbite | UE5 | 本方案 |
|---|---:|---:|---:|---:|---:|---|
| RenderWorld 所有权 | 主 | — | — | — | 辅 | 容量 1 双向移交 |
| texture 子资源 | — | 部分 | view range | 公开资料有限 | 主 | mip/layer/plane 完整跟踪 |
| temporal slice | — | history | 主 | history 概念 | extract/register | 自动槽 + 跨帧边 + 迁移 |
| 版本与裁剪 | — | 强 | 强 | 强 | 强 | 范围版本覆盖映射 |
| 多队列批次 | 后端抽象 | 强 | 主 | 强 | 强 | batch DAG + timeline |
| Raster fusion | — | 主 | 可扩展 | — | 强 | 兼容规则 + 原生编码 |
| 诊断 | ECS trace | 图日志 | visualizer | 图可视化 | Insights | 编译产物直接序列化 |

# 3. 总体架构

[[FIGURE:figures/architecture.png]]

## 3.1 分层与单向依赖

| 模块 | 输入 | 输出 | 禁止事项 |
|---|---|---|---|
| Feature | RenderWorld 数据、稳定资源 id | Pass 声明、虚拟资源、回调 | 直接保存物理资源、手写 barrier |
| Public API | 描述、view range、history index | 索引句柄、Builder | 暴露后端枚举或指针 |
| Graph Core | Pass/资源/访问记录 | 范围版本 DAG、根、验证结果 | 依赖 Vulkan/D3D12 |
| Compiler | DAG、capability、外部状态 | batch DAG、分配、屏障、记录区间 | 调用业务 shader 逻辑 |
| Runtime | immutable compiled graph | command lists、提交、completion | 修改编译结果 |
| Backend | barrier intent、allocation intent | 原生对象与命令 | 决定业务依赖 |
| RenderWorld | 提取结果、GPU completion | 本帧图、temporal registry、回收 | 访问主世界可变对象 |
| Diagnostics | 编译产物、运行时事件 | DOT/JSON/timeline/capture | 重新推导另一套图 |

## 3.2 核心原则

1. **声明意图，不声明 API 状态。** 功能代码声明 `ResourceAccess`，后端映射 layout/state/stage/access。
2. **资源与视图分离。** 资源描述物理能力，视图描述本次访问范围和格式解释。
3. **写入产生范围版本。** 逻辑句柄是版本入口；内部 producer map 可按范围继承和覆盖。
4. **执行期解析。** 只有声明过访问的 Pass 回调才能把 handle/view 解析成原生对象。
5. **确定性。** 相同图结构、capability 和外部 generation 必须产生相同排序、分配与哈希。
6. **不能证明就保守。** 无 happens-before 则不别名；无法证明全覆盖则保留旧内容依赖。
7. **跨帧不是图外特殊代码。** 历史等待、迁移、resolve、readback 都必须可见、可裁剪、可诊断。
8. **后端能力差异用 capability 与 fallback 表达。** 核心不出现 Vulkan-only 或 D3D12-only 业务分支。

## 3.3 关键对象

```text
RenderWorld
 ├─ RenderGraphRuntime
 │   ├─ FrameContextRing
 │   ├─ CompilerWorkspacePool
 │   ├─ PhysicalResourceCache
 │   ├─ DescriptorAllocator
 │   ├─ ResidencyManager
 │   └─ DeferredReleaseQueue
 ├─ TemporalRegistry
 │   ├─ TemporalTextureFamily[]
 │   └─ TemporalBufferFamily[]
 ├─ PipelineCache / SlangProgramRegistry
 ├─ UploadRing / ReadbackPool / QueryPool
 └─ DiagnosticsHub

RenderGraph (per recording)
 ├─ ResourceTable
 ├─ ViewTable
 ├─ PassTable
 ├─ AccessTable
 ├─ SourceLocationTable
 └─ Blackboard / ContextContainer

Compiled (immutable)
 ├─ LivePasses
 ├─ ResourceVersions / ProducerMaps
 ├─ CommandBatches
 ├─ BarrierBatches
 ├─ PhysicalAllocations
 ├─ RecordRanges
 └─ DiagnosticSnapshot
```

## 3.4 一帧边界

CPU 世界移交深度、GPU in-flight 深度和 temporal history 深度是三个独立量：

- CPU RenderWorld 通道容量固定为 1；
- GPU `FrameContext` 通常为 2～4，由 swapchain 和平台决定；
- `HistoryCount` 由效果决定，例如 TAA 为 1，reservoir 可能为 2～8；
- temporal 物理槽复用同时受 in-flight completion 和 history lookback 约束。

# 4. 每帧生命周期与线程边界

## 4.1 录制与执行顺序

一次 `BeginRecording` 到 `EndFrame` 包含以下处理环节；这是单次执行算法，不是项目分期：

1. RenderWorld 应用本帧提取命令，更新稳定 id 与 generation；
2. temporal registry 读取 GPU completed values，回收可复用槽与旧 generation；
3. `BeginRecording` 重置逻辑表和线程本地 arena；
4. 各 Feature 用普通 C# 控制流创建资源和 Pass；
5. `EndRecordingAndExecute` 冻结图，禁止继续写 Builder；
6. 编译器归一化 texture/buffer range，构建范围版本依赖；
7. temporal 解析当前/历史槽并插入跨帧 producer 或迁移 Pass；
8. 反向裁剪、稳定排序、队列安排、寿命和物理分配；
9. 合成屏障、批次、wait/signal 与并行记录区间；
10. worker 记录命令，提交线程按 batch DAG 提交；
11. 将每个 temporal/external 子资源的最终状态与 completion 写回 registry；
12. `EndFrame` 回收临时表、推进 descriptor ring、执行延迟销毁检查。

## 4.2 线程所有权

| 数据 | 主线程 | 渲染线程 | worker | GPU completion 回调 |
|---|---:|---:|---:|---:|
| 游戏世界 | 读写 | 禁止 | 禁止 | 禁止 |
| RenderWorld | Extract 时独占 | 其余时独占 | 只读快照 | 禁止 |
| Graph 声明表 | 禁止 | 构建/冻结 | 可并行 setup 时写线程分片 | 禁止 |
| Compiled | 禁止 | 创建/提交 | 只读 | 只读 id |
| Backend device/queue | 禁止 | 独占管理 | 命令 allocator/pool 局部 | 回收队列 |
| TemporalRegistry | 禁止 | 独占写 | 只读已解析槽 | 只写 completion mailbox |

## 4.3 帧编号

必须区分：

- `SimulationFrameId`：游戏逻辑更新；
- `RenderFrameId` / `FrameIndex`：被渲染的提取快照；
- `SubmissionId`：每个 queue submit 的全局单调 id；
- `GpuCompletedFrameIndex`：RPS 风格的安全帧边界提示；
- queue timeline value：真正决定某物理槽是否可复用的后端完成值；
- `ResetHistory`：相机 cut、场景重载或显式历史失效标志；RenderWorld 将其转换为内部单调 generation。

任何诊断记录都必须标明使用哪个编号，禁止只写模糊的“frame”。

# 5. Bevy 式 RenderWorld

[[FIGURE:figures/renderworld.png]]

## 5.1 通道配置

```csharp
var options = new BoundedChannelOptions(1)
{
    SingleReader = true,
    SingleWriter = true,
    FullMode = BoundedChannelFullMode.Wait,
    AllowSynchronousContinuations = false,
};
```

主→渲染与渲染→主各一条通道。对象在通道内即表示“无人持有但所有权正在传递”；任何线程不能在发送后保留可变引用。容量 1 既是背压也是状态连续性约束，不能改成 `DropOldest` 或 `DropWrite`。[B1][N3]

## 5.2 RenderWorld 内容

RenderWorld 不只是提取后的 ECS，它还必须持有跨帧 GPU 系统：

- stable resource id / asset generation 映射；
- temporal registry 与历史有效性；
- physical resource cache 与 residency manager；
- descriptor heap/arena 与 bindless slot table；
- pipeline cache、shader hot-reload generation；
- deferred release queue；
- upload/readback/query pools；
- graph compiler workspace pool；
- device-lost recovery journal；
- diagnostics ring 与 capture controller。

这些对象必须只由渲染线程写，因为它们与 GPU completion 和后端线程亲和性紧密相关。

## 5.3 Extract 数据规则

- 小型 POD 值复制；
- 大数组用 immutable snapshot、版本化 chunk 或 ownership transfer；
- 主世界不直接销毁 GPU 对象，只提交稳定 id 的释放意图；
- 资产热重载用 `AssetId + Generation`，旧 generation 按 completion 延迟释放；
- camera/view 使用稳定 `ViewId`，temporal family key 不能使用本帧数组索引；
- extract 命令必须可幂等应用，设备恢复时允许重放；
- 图构建只读取 RenderWorld 的渲染副本，不回读游戏世界。

## 5.4 关闭、异常与设备丢失

正常关闭：停止产生新模拟帧 → 完成发送端 → 等待 RenderWorld 返回 → 停止渲染线程 → 在正确线程销毁 device/queue/descriptor objects。

异常：渲染线程把原始异常和 `SubmissionId` 写入 fault channel；主线程在下一交接点观察并停止继续发送。不能让 RenderWorld 永久留在故障线程。

设备丢失：停止新提交但继续交还 RenderWorld；标记所有物理资源 generation 无效；保留逻辑 temporal family 和重建描述；重建设备后按依赖重建 persistent、bindless、RTAS 与历史资源。历史内容默认失效，除非平台提供可验证的保留机制。

## 5.5 同步模式

测试、工具和线程受限平台使用同一 schedule 的同步适配器：主线程直接调用 Extract → Prepare → Build → Execute。同步模式不能跳过 temporal、屏障、validation 或 completion 模拟，否则与异步模式无法一致测试。

# 6. 公开 API 与命名约束

## 6.1 命名门禁

所有 public 类型、方法、属性和 enum member 都进入证据生成器：

1. 用精确标识符执行 GitHub code search；
2. 保存实际源码 URL、仓库、文件、匹配形式和搜索 URL；
3. `exact` 表示同拼写真实出现；`case_adapted` 只允许大小写适配；
4. 搜索结果与本语义明显不相关时不得作为唯一证据；
5. 任何缺失证据使 CI 失败；
6. 公共 API 禁止使用泛化的 `Plan` / `Use` 作为结尾；Slang 和 Work Graph 的 `Program` 是官方领域术语，可以保留；D3D12 state object authoring 使用 `Authoring` / `Manifest` 命名，不再使用 `Plan`。
7. 文档、代码和 JSON 的标识符集合必须完全一致。

API 名称优先吸收 Unity RenderGraph、UE/RPS/KDGpu/Godot、Vortice、Silk.NET 等真实工程中的已有词汇。证据 JSON 不等于“来源项目有完全相同语义”，只证明名称不是凭空创造；语义以本文契约为准。

## 6.2 API 设计要点

- `RenderGraph` 负责 recording 生命周期、资源创建/导入和 Pass 添加；
- `RenderGraphBuilder` 是 `ref struct` 的理想实现形态；契约草案暂用 struct 便于展示；
- `TextureHandle` / `BufferHandle` 是 graph epoch + resource index + version 的压缩句柄；
- `TextureViewDesc` / `BufferViewDesc` 承载范围与 `HistoryIndex`；
- `TextureSubresourceRange.All` / `BufferRange.All` 仅是快捷值；内部总要归一化；
- attachment 通过 `RenderAttachmentDesc` 表达 load/store/clear/resolve；
- `ImportResourceParams` 显式给出初始/最终 access、queue 和外部 timeline；
- `RenderGraphParameters` 同时给出 frame、completed frame、history reset、reference size；
- 原生资源只能由 `RenderGraphContext` 在声明了对应访问的回调内解析。

## 6.3 范围 API 示例

```csharp
var hiZ = graph.CreateTexture(new TextureDesc
{
    Name = "HiZ",
    Width = width,
    Height = height,
    MipCount = mipCount,
    Format = Format.R16G16B16A16Float,
    Flags = TextureFlags.Aliasable,
});

for (var mip = 1; mip < mipCount; mip++)
{
    using var builder = graph.AddPass<HiZPassData>(
        $"HiZ Mip {mip}", QueueType.Compute, out var data);

    var srcView = new TextureViewDesc
    {
        Range = new TextureSubresourceRange
        {
            MipIndex = mip - 1,
            NumMips = 1,
            ArraySlice = 0,
            NumArraySlices = 1,
            PlaneSlice = 0,
            NumPlaneSlices = 1,
            Aspect = TextureAspect.Color,
        },
    };

    var dstView = srcView with
    {
        Range = srcView.Range with { MipIndex = mip },
    };

    data.Source = builder.ReadTexture(hiZ, srcView);
    data.Target = builder.WriteTexture(hiZ, dstView, flags: AccessFlags.WriteAll);
    builder.EnableAsyncCompute(true);
    builder.SetRenderFunc(static (pass, context) => { /* dispatch */ });
}
```

该写法允许不同 mip 拥有独立 producer 和 queue 状态。`WriteAll` 只表示覆盖 `dstView.Range` 的全部 texel，不表示覆盖整张纹理。

## 6.4 temporal API 示例

```csharp
var history = graph.CreateTexture(new TextureDesc
{
    Name = "TAA History",
    Width = width,
    Height = height,
    MipCount = 1,
    HistoryCount = 2,
    ResizeMode = ResizeMode.Resample,
    Format = Format.R16G16B16A16Float,
    Flags = TextureFlags.Persistent | TextureFlags.Bindless,
});

var previous = new TextureViewDesc
{
    Range = TextureSubresourceRange.All,
    HistoryIndex = 1,
};

var current = previous with { HistoryIndex = 0 };

data.Previous = builder.ReadTexture(history, previous, ResourceAccess.ShaderRead);
data.Current = builder.WriteTexture(history, current,
    ResourceAccess.ShaderWrite, AccessFlags.WriteAll);
```

Feature 不选择物理槽、不等待 fence、不导入上一帧对象。编译器与 temporal registry 自动完成这些工作。

## 6.5 生命周期限制

- Builder 只能在其所属 Pass setup 范围内使用；
- `EndRecordingAndExecute` 后所有 Builder 失效；
- handle 包含 graph epoch，跨 graph 使用立即报错；
- `HistoryIndex > HistoryCount` 报错；
- 只有 `HistoryIndex=0` 可写，历史 slice 默认只读；
- 同一 Pass 的重叠 read/write 必须使用 `ReadWrite*` 或明确声明允许的 feedback 模式；
- `GetTextureView` 只能解析该 Pass 已声明范围的子集，不能扩大范围；
- `AllowGlobalStateModification` 会把 Pass 设为不可并行、不可融合并形成显式副作用根。

# 7. 资源、视图、版本与物理对象

## 7.1 四层身份

| 层 | 示例 | 生命周期 | 是否含后端对象 |
|---|---|---|---:|
| 逻辑资源 | TAA History family | 多帧或单图 | 否 |
| 逻辑版本 | History 当前帧写后的版本 | 图内 | 否 |
| 资源视图 | mip 2、layer 4、depth plane | 单次访问或缓存 | 否/延迟 |
| 物理对象 | VkImage / ID3D12Resource | completion 管理 | 是 |

禁止用一个“Texture”对象同时承担四层角色。否则局部版本、历史槽、view cache 和别名无法分离。

## 7.2 句柄编码

建议 64 位句柄：

```text
bits  0..23  resource index
bits 24..39  version index
bits 40..55  graph epoch
bits 56..59  resource kind
bits 60..63  debug flags / reserved
```

实际位宽可调整，但必须满足：

- Release 构建仍能检测跨 graph epoch；
- version index 不与 physical slot 混淆；
- temporal `HistoryIndex` 不编码进 handle，而在 view 中解析；
- null handle 与合法 index 0 可区分。

## 7.3 ResourceRecord

```csharp
internal struct TextureRecord
{
    public TextureDesc Desc;
    public StableResourceId StableId;
    public int FirstVersion;
    public int VersionCount;
    public int TemporalFamilyIndex;
    public TextureStateStorage StateStorage;
    public PhysicalBinding Binding;
    public ResourceFlags InternalFlags;
}
```

`TextureStateStorage` 是 tagged union：

- `Uniform`：一个 producer/state/lifetime 覆盖全部子资源；
- `Expanded`：从 pool 租用 `SubresourceCell[]`；
- `SparseIntervals`：可选优化，适合超大 array texture 且只触及少数 layer；
- `ExternalSnapshot`：导入资源的初始 per-range 状态。

## 7.4 ViewRecord

View 不应在构图时立即创建后端 SRV/UAV/RTV/DSV。`ViewRecord` 只保存规范化描述和 hash；执行时由 descriptor/view cache 解析。相同 physical object + generation + normalized view + descriptor class 才可复用原生 view。

## 7.5 版本语义

逻辑“写返回新句柄”只是前端便利。内部版本必须能表达局部继承：

```text
V0 producer map:
  [all] -> Import/Clear

write mip 2 creates V1:
  [mip 0..1] -> producer from V0
  [mip 2]    -> Pass A
  [mip 3..N] -> producer from V0
```

读取 V1 的 mip 0 依赖旧 producer，读取 mip 2 依赖 Pass A。不能把 V1 的单个 `ProducerPassIndex` 当作整资源 producer。

## 7.6 稳定资源身份

跨帧 family key 建议由以下字段组成：

```text
FeatureTypeId + ViewId + SemanticId + UserKey
```

名称仅用于诊断，不参与稳定身份。动态分辨率尺寸不应改变 stable id，只改变 descriptor generation。相机销毁、view identity 复用和 split-screen slot 变化必须通过 generation 或显式 release 避免历史串线。

# 8. 任意 mip/layer/plane 子资源跟踪

[[FIGURE:figures/subresource_tracking.png]]

## 8.1 范围规范化

输入范围允许使用 `All`、0 数量表示剩余范围等便利形式，但进入图核心前必须规范化为闭开区间：

```text
mip    = [MipIndex, MipIndex + NumMips)
layer  = [ArraySlice, ArraySlice + NumArraySlices)
plane  = [PlaneSlice, PlaneSlice + NumPlaneSlices)
```

规范化规则：

1. `All` 展开成描述中的完整 mip/layer/plane；
2. cube 的 array layer 以 face 为最小单位；cube array 的 layerCount = cubeCount × 6；
3. 3D texture 的 depth slice 可用于 view，但不成为独立同步状态单元；状态粒度仍是整个 mip；
4. depth/stencil 格式将 `Aspect` 映射到 plane；同时给出互相矛盾的 `Aspect` 与 `PlaneSlice` 必须报错；
5. multi-planar video format 将 Plane0/1/2 映射到实际 plane；
6. view format 必须与资源格式兼容；typeless reinterpret 由 capability 检查；
7. 超界、空范围、MSAA texture 非法 mip、3D texture 非法 array layer 均在冻结时失败。

## 8.2 子资源索引

密集展开使用：

```text
index = ((plane * layerCount) + layer) * mipCount + mip
```

选择 mip 为最内层，使 mip-chain 连续访问具有良好局部性。对常见 1 plane、1 layer、≤16 mip texture，整个状态数组很小；对大型 array texture 使用 lazy page 或稀疏 interval，避免为未触及 layer 分配数万单元。

## 8.3 状态单元

```csharp
internal struct SubresourceCell
{
    public int Version;
    public int LastWriter;
    public ReaderSet Readers;
    public ResourceAccess Access;
    public QueueType Queue;
    public int FirstBatch;
    public int LastBatch;
    public ulong CompletionValue;
    public ushort Generation;
    public SubresourceFlags Flags;
}
```

`ReaderSet` 不应默认为 `HashSet<int>`。推荐：

- 0 reader：inline sentinel；
- 1 reader：inline pass index；
- 多 reader：arena 中连续列表；
- 编译完当前版本 writer 后可清空旧 readers；
- 若只需要建立 WAR 边，可压缩为每 queue 的最后 reader frontier，但 debug 模式保留完整集合用于解释。

## 8.4 统一快路径

绝大多数纹理只做整资源访问。`TextureStateStorage.Uniform` 保存一个 cell，不分配数组。出现以下任一情况才展开：

- 非全范围访问；
- depth 与 stencil 分开；
- 同一物理纹理的不同 plane 当前状态不同；
- 外部导入给出多段初始状态；
- temporal slot 保存了多段最终状态；
- 局部 migration 或局部 readback。

展开时复制 uniform cell 到所需单元；之后如果所有单元再次等价，可以在 `EndFrame` 或 cache compact 时折叠回 uniform，但不在热编译路径进行 O(N) 折叠。

## 8.5 范围运算

需要无分配实现以下原语：

```text
Normalize(range, desc)
Intersects(a, b)
Intersection(a, b)
Contains(a, b)
Subtract(a, b) -> 0..6 boxes
EnumerateCells(range)
Coalesce(cells with equal state) -> ranges
```

三维正交 box 相减最多产生六个非重叠 box。对 dense array 可直接遍历 cell；对 sparse range map 使用 box split。不要把 range 转成每个 texel，状态粒度仅到 mip/layer/plane。

## 8.6 访问规则

对每个相交状态单元应用：

| 新访问 | 需要的依赖 | 状态更新 |
|---|---|---|
| Read | last writer → current | 加入 readers，更新 access/queue |
| Write | last writer → current；所有 readers → current | current 成为 writer，清空 readers |
| ReadWrite | last writer → current；所有 readers → current | 保留内容依赖后 current 成 writer |
| WriteAll/Discard | 所有 readers → current；旧 writer 仅在状态/ownership 需要时参与 | 当前覆盖范围内容不依赖旧值 |
| Copy/Resolve source | writer → current | 作为 read |
| Copy/Resolve destination | writer/readers → current | 按是否全覆盖决定 discard |
| Attachment Load | writer → current | read + write |
| Attachment Clear/DontCare | readers → current | 覆盖范围 discard |

注意：`Discard` 消除的是**内容依赖**，不自动消除 queue ownership、aliasing、UAV execution dependency 或旧 reader 的 WAR。后端仍可能需要 barrier。

## 8.7 部分写的版本覆盖

假设 V0 全范围由 P0 产生，Pass P1 写 mip [2,4)：

```text
V1 = Overlay(V0, [2,4) -> P1)
```

实现可选：

- persistent interval tree：版本共享未变区间；
- copy-on-write range map：小范围列表，超过阈值转 dense；
- dense producer array：按 cell 复制索引；适合子资源数小的纹理。

推荐混合：≤64 cells 使用 stack/pool dense array；更大 texture 使用 immutable range segments，访问密度超过 25% 再展开 dense。阈值由 benchmark 固定。

## 8.8 物理重命名与局部版本

逻辑版本不等于自动拥有另一份物理 texture。若旧版本和新版本的重叠物理范围必须同时存活，编译器必须作出可解释选择：

1. 若执行偏序可序列化，复用同一物理对象；
2. 若允许 physical rename，分配新对象并把未覆盖范围 copy 到新对象；
3. 若 Pass 声明 `WriteAll` 覆盖全部所需范围，可省略 copy；
4. 若 descriptor 不允许 copy/rename，编译失败并指出冲突 Pass 与范围。

禁止默默让两个逻辑版本指向同一范围并并发读写。

## 8.9 屏障合并

编译器先按 cell 生成 transition intent，再按以下条件合并：

- 同一物理对象与 generation；
- before/after access、queue、layout/state 相同；
- 相邻 mip 或 layer 能表示成矩形 range；
- 不跨越需要单独 ownership transfer 的范围；
- depth/stencil plane 只有在后端允许联合 barrier 时合并。

目标不是最少 barrier 条数，而是在不扩大 hazard 的情况下减少后端调用和结构大小。

## 8.10 必测场景

- mip-chain 逐级生成，mip N 只依赖 N-1；
- mip 0 graphics 写、mip 4 compute 写，两者并行；
- cube 六面不同 Pass 写，随后整 cube sample；
- depth read + stencil write；
- array shadow atlas 单 layer 更新；
- 局部 write 后读取未覆盖 mip 仍指向旧 producer；
- 局部 discard 不错误消除 WAR；
- temporal history 的不同子资源最终状态被下一帧正确恢复；
- Vulkan 与 D3D12 合并后的 barrier intent 等价。

# 9. Buffer 区间跟踪

## 9.1 为什么不能只做 texture 子资源

3A 渲染大量使用聚合 buffer：instance data、meshlet、visibility、indirect arguments、count buffer、skin cache、particle pool、light lists、ray tracing instance descriptors。整 buffer 跟踪会把无关区间串行化，并让 async compute 与 copy queue 失去价值。

## 9.2 规范化单位

`BufferRange` 以 byte offset/size 作为底层真值。`BufferViewDesc.Stride` 允许结构化视图；element range 在 Builder 入口转换成 byte range。规则：

- `Size=-1` 表示到 buffer 末尾；
- offset/size 必须满足资源和后端要求的对齐；
- constant buffer、AS scratch、indirect arguments 可有更严格对齐；
- raw/structured/typed view 的格式与 stride 兼容性必须验证；
- counter buffer 是独立逻辑 range，不与 payload 隐式共享状态。

## 9.3 IntervalMap

每个 buffer 维护不重叠、有序区间：

```text
[0, 4096)      -> state A / producer P0
[4096, 8192)   -> state B / producer P4
[8192, size)   -> state A / producer P0
```

写入 `[3000,5000)` 时分割为至多五段并替换中间 producer。相邻状态完全相同的段立即合并。实现使用 pool-backed sorted vector；段数超过阈值时转 interval tree。多数帧段数较小，vector 比树更缓存友好。

## 9.4 特殊 access

- `IndirectArguments`：写入 compute/copy 后，draw/dispatch indirect 读取；
- `Predication`：状态映射不同于普通 indirect；
- `AccelerationStructureRead/Write`：AS storage 和 scratch 的 hazard 分开；
- `HostRead/HostWrite`：需要 non-coherent flush/invalidate 和 submission completion；
- `SparseBinding`：与普通 GPU queue 执行域分开，但进入 batch DAG；
- append/counter：payload 和 counter 可独立 range，UAV counter barrier 不能遗漏。

## 9.5 同一 Pass 内重叠

同一 Pass 声明重叠 read + write 时：

- 若 shader 真实执行 read-modify-write，必须声明 `ReadWriteBuffer`；
- 若两个绑定逻辑上不重叠但 range 描述重叠，validation 报错；
- 若后端需要 UAV barrier，Pass 内部多 dispatch 之间由 CommandBuffer API 显式插入 local barrier；图只管理 Pass 边界；
- 一个 Pass 内不能通过两个不同 handle 绕过同一物理 range 的冲突检测。

# 10. 跨帧 temporal family 与自动迁移

[[FIGURE:figures/temporal_resources.png]]

## 10.1 定义

Temporal family 是 RenderWorld 中持久存在的逻辑资源族：

```csharp
internal sealed class TemporalTextureFamily
{
    public StableResourceId StableId;
    public TextureDesc LogicalDesc;
    public uint Generation;
    public uint HistoryResetId;
    public TemporalSlot[] Slots;
    public int HistoryCount;
    public int MaxFramesInFlight;
    public ValidRegion ValidRegion;
}
```

每个 `TemporalSlot` 至少保存：

- physical resource / allocation / descriptor generation；
- 对应 `RenderFrameIndex`；
- per-subresource final access、queue、last writer；
- 每个相关 queue 的 completion value；
- contents valid flag / valid region；
- descriptor generation 与 migration source；
- bindless slot、residency state、debug name。

## 10.2 槽数量与解析

保守配置：

```text
slotCount >= MaxFramesInFlight + MaxHistoryLookback
```

该公式可避免任何历史读取与在途写槽碰撞，但可能多占显存。实际实现允许 completion-aware 压缩：选择目标槽时检查该槽不再被任何在途 batch 引用，且不再属于当前需要保留的 history window。若无可用槽：

1. 尝试 residency/alias 策略不能破坏历史；
2. 可分配额外 overflow slot，并记录显存压力；
3. 到 hard budget 时等待最早 completion；
4. 不允许覆盖尚未完成或仍在历史窗口中的槽。

解析：

```text
HistoryIndex 0 -> 本帧目标槽
HistoryIndex k -> stable family 中 RenderFrameIndex - k 的已完成/在途槽
```

历史读允许引用尚在 GPU 执行中的前帧槽，只要批次 DAG 插入正确 timeline wait；不要求 CPU 阻塞。

## 10.3 跨帧 producer 边

对历史读取，编译器创建 `ExternalProducerNode`：

```text
TemporalSlot.lastWriter(queue Q, value V, range R)
        -> current pass read(range intersection)
```

该节点不执行命令，但生成：

- queue wait，若当前 queue 与 producer queue/提交不同；
- initial barrier state，来自槽的 per-range final state；
- diagnostic edge，标明 family、history index、frame index、completion value；
- lifetime pin，直到当前 batch completion。

如果历史内容无效，读取不能静默返回未初始化内存。按资源的初始化策略插入 clear/initialize Pass，或让 Pass 通过一个明确的 `history valid` 常量走无历史分支。

## 10.4 当前写与最终状态回写

`HistoryIndex=0` 的写入解析到本帧槽。图执行编译完成后，不是 CPU 提交后立即标记可复用，而是在每个最终 producer batch 上记录 completion token。每个最终子资源状态回写：

```text
range -> { access, queue, batch, timeline value, generation, valid }
```

下一帧以该状态作为 imported initial state。若同一槽不同 mip/layer 最终状态不同，registry 必须保存分段状态，不能折叠成整资源。

## 10.5 描述变化分类

| 变化 | 行为 | 注入工作 |
|---|---|---|
| 完全一致 | 复用槽 | 无 |
| 仅 debug name | 复用 | 更新标签 |
| 尺寸变化且格式兼容 | 依 `ResizeMode` | Copy overlap / Resample / Clear / Discard |
| mip 数变化 | 保留公共 mip，补齐新 mip | Copy + GenerateMips 或 Clear |
| array layer 增加 | 保留公共 layer | 分范围 Copy/Clear |
| format 可 reinterpret | 复用或新 view | capability 检查 |
| format 可转换 | 新 generation | Convert Pass |
| sample count 变化 | 新 generation | Resolve/Expand/Clear |
| usage flags 增加但物理对象不兼容 | 新 generation | Copy/Convert |
| sparse tile geometry 变化 | 新 generation | page remap + content migration |
| device generation 变化 | 全部重建 | Initialize；历史无效 |

## 10.6 Migration Pass

迁移不得隐藏在 resource cache 中。编译器在图冻结后、依赖分析前注入内部 Pass：

```text
TemporalMigration/TAA History/g42->g43
  read  old generation, valid common range
  write new generation, target range
  queue Copy or Compute
  flags: NeverCull only when target is live
```

选择规则：

- exact overlap copy：Copy queue；
- resize filter：Compute queue，滤波器由效果 policy 或默认 Catmull-Rom/box；
- MSAA→single：Resolve；
- single→MSAA：通常 clear 或专用 expand shader，禁止假装 copy；
- format conversion：Compute；
- depth history：只允许后端支持的 copy/resolve，否则 clear；
- migration 本身可被裁剪：如果本帧没有读取历史且当前写 `WriteAll`，旧内容无需迁移。

## 10.7 有效区域

动态分辨率下，历史 texture 可能按最大尺寸分配但只有部分 viewport 有效。family 保存 `ValidRegion`：

- logical viewport / scissor；
- scale 与 jitter convention；
- exposure/pre-exposure metadata；
- projection/view transform fingerprint；
- shader feature key。

迁移和采样只对 valid region 建立内容语义。分辨率变小可保留 allocation；变大时新增区域必须初始化。

## 10.8 失效

以下事件令 `ResetHistory=true`，并由 RenderWorld 提升内部 history reset generation：

- camera cut / teleport；
- view identity 改变；
- world origin rebasing 超过阈值；
- shading path 或关键 shader permutation 改变；
- temporal algorithm version 改变；
- device lost；
- 用户显式 reset；
- 资源 descriptor 不可迁移变化。

失效可以是整 family，也可以是 subresource range。历史有效性必须作为 PassData 输入，不能让 shader 通过猜测 frame number 判断。

## 10.9 外部历史与第三方 SDK

DLSS/FSR/XeSS、视频解码器、平台 compositor 可能拥有图外资源。对它们使用 `ImportResourceParams`：

- 导入时给出 initial access/queue/wait；
- Pass 明确声明读写 range；
- 图结束输出 final access/queue/signal；
- 外部 owner 通过返回的 completion token 继续使用；
- 若 SDK 自己管理 history，目标框架不再复制一套 temporal family，但仍追踪每帧外部状态。

普通引擎历史资源不得滥用 import/export 绕过 temporal registry。

## 10.10 测试矩阵

- TAA 前一帧读、本帧写；
- HistoryIndex 1～4；
- 三帧 in-flight + 人为延迟 GPU；
- async compute 前帧写、graphics 本帧读；
- 每帧动态分辨率变化；
- resize 过程中 copy/resample/clear 策略；
- camera cut 与 history reset；
- format/sample count 变化；
- partial mip history；
- device lost/recreate；
- memory budget 导致 overflow slot 与等待；
- capture/replay 中物理槽分配可复现。

# 11. 图编译处理链

[[FIGURE:figures/compiler_chain.png]]

以下编号只表示一次 `EndRecordingAndExecute` 内的处理顺序。

## 11.1 冻结与结构验证

- 所有 Builder 已 Dispose；
- Pass 有执行回调或明确的内部执行器；
- handle epoch、type、version 合法；
- view range 与 format 合法；
- history index 与读写规则合法；
- attachment index、sample count、size、view count 匹配；
- external import 初始/最终状态完整；
- callback 捕获策略符合无分配要求。

## 11.2 访问归一化

把公开调用转为统一 `AccessRecord`：

```csharp
internal struct AccessRecord
{
    public int PassIndex;
    public int ResourceIndex;
    public int VersionIndex;
    public int ViewIndex;
    public ResourceKind Kind;
    public ResourceAccess Access;
    public AccessFlags Flags;
    public QueueType RequestedQueue;
    public int HistoryIndex;
    public NormalizedRange Range;
}
```

Attachment Load 转 read+write；Clear/DontCare 转 write/discard；resolve 拆成 source read 与 destination write；present、readback、external signal 形成副作用根。

## 11.3 temporal 解析与内部 Pass 注入

- 稳定 family key 查找/创建；
- 选择 current/history slot；
- 注入 migration、initialize、resolve、generate mip、query resolve；
- 创建 external producer nodes；
- 把物理 generation 和 initial range state 绑定到逻辑资源。

内部 Pass 与用户 Pass 使用同一表，只带 `InternalPassKind` 供诊断分类。

## 11.4 范围版本依赖

按第 8、9 章算法处理每条 AccessRecord。边记录原因：

```text
producer pass/range/version
consumer pass/range/version
hazard RAW/WAR/WAW/ownership/alias/external
source declaration location
```

多条相同 pass pair 的边可在执行图中合并，但 diagnostic detail 保留每个资源范围。

## 11.5 根识别与反向裁剪

根包括：

- Present；
- external final state/signal；
- readback/query result；
- temporal current slot 的 live writer；
- `AllowPassCulling(false)`；
- global state modification；
- debug capture 强制保留；
- sparse residency update 对本帧 live resource 的影响。

从根沿 producer 边反向标记。migration 只有目标资源活跃且旧内容被需要时保留。`WriteAll` 的当前帧 Pass 可裁剪不必要的历史 migration。

## 11.6 确定性拓扑排序

Kahn 算法使用稳定 priority key：

```text
(explicit order group, declaration ordinal, stable pass type id, name hash)
```

不能依赖 dictionary iteration、线程完成顺序或对象地址。环诊断输出最短或近似最短 cycle，列出每条边的资源范围和 hazard。

## 11.7 队列选择与异步窗口

- Pass 请求 queue 只是候选；
- 检查命令能力、resource ownership、平台 queue family；
- compute/copy 不可用时回落 graphics；
- 找出跨队列 producer frontier 与 consumer frontier；
- 在不改变依赖的前提下做 early signal / late wait；
- 估计资源寿命扩张和显存峰值；超过预算可回落 graphics；
- 诊断记录“请求队列、实际队列、回落原因”。

## 11.8 Raster fusion

识别 attachment 集、subresource range、sample count、load/store、view mask、VRS、feedback loop、query 和 global state。兼容的相邻 raster Pass 合并成 native render-pass group；不兼容时保持 dynamic rendering 或独立 render pass。

## 11.9 寿命与 batch 偏序

资源范围寿命用 batch DAG 表达，不用单一整数：

```text
first frontier = 最早可达 batch 集
last frontier  = 最晚消费者 batch 集
```

为了高效分配，可计算 topological interval 作快速排除，再用 happens-before 查询证明别名安全。跨队列无路径的两个 range 即使线性序号不重叠也可能并发，禁止别名。

## 11.10 物理分配、驻留与 descriptor

- 选择 persistent / temporal / transient / external 类别；
- 根据 memory class、alignment、flags、format、samples、tiling 生成兼容 key；
- transient range 做 best-fit/linear-scan + alias proof；
- 预算不足时触发 cache trim、residency eviction、overflow telemetry 或受控等待；
- 创建 physical binding 与 view descriptors；
- bindless resource 使用 stable slot + generation validation。

## 11.11 屏障与批次

先生成 API 无关 intent：

```text
resource/generation/range
before access/queue
 after access/queue
source batch / destination batch
kind transition / UAV / alias / acquire / release / split
```

再合并、消冗余、形成 command batches。每 batch 有 queue、Pass 范围、barrier list、wait values、signal value、record ranges。

## 11.12 不可变产物

`Compiled` 一旦发布给 worker 便不可修改。执行和诊断都读取它；任何动态决定（如 pipeline 尚未准备）必须在编译前作为 fallback Pass 或 skip policy 固化，不能由 worker 临时改图。

# 12. 依赖、裁剪、排序与缓存

## 12.1 Hazard 规则

| 前访问 | 后访问 | 是否需要边 | 说明 |
|---|---|---:|---|
| Read | Read | 通常否 | queue/layout 兼容时共享 |
| Write | Read | 是 | RAW |
| Read | Write | 是 | WAR，保护 reader 完成 |
| Write | Write | 是 | WAW，除非有可证明独立范围 |
| UAV Write | UAV Read/Write | 是 | 即使 state 相同也可能需要 UAV barrier |
| Alias old | Alias new | 是 | allocation ownership 改变 |
| External producer | Read/Write | 是 | timeline wait + initial transition |
| Sparse bind | Resource access | 是 | residency 可见性 |

仅在 normalized range 相交时产生资源 hazard。不同 physical resource 但共享 alias allocation 时另加 alias edge。

## 12.2 副作用与黑板

Feature 间通过强类型 `ContextContainer`/blackboard 交换逻辑 handles，不互相持有实现对象。blackboard value 生命周期仅一图；跨帧数据必须进入 RenderWorld 或 temporal family。

副作用必须显式：present、external signal、readback callback、query resolve、global state、debug marker output。普通日志或 CPU 数据结构修改不能偷偷发生在可裁剪 Pass setup 中；setup 应是构图纯函数，执行回调才产生 GPU 工作。

## 12.3 图模板缓存

缓存 key 包含：

- stable pass type/order 与条件分支结果；
- resource desc 的结构字段，不含每帧常量；
- normalized accesses 与 queue requests；
- capability key、backend barrier model；
- dynamic size expression 结构；
- shader/pipeline layout generation；
- temporal descriptor generation class。

缓存可复用拓扑、producer map 模板、batch 结构和 allocation layout，但每帧仍要绑定：外部 physical object、temporal slot、completed values、dynamic dimensions、residency 和 actual pipeline readiness。

缓存命中必须产生与全编译相同的 diagnostic snapshot hash。Debug 模式随机抽样双编译比对，防止缓存失效键遗漏。

## 12.4 动态图与条件 Pass

普通 C# `if` 决定图结构；条件变化自然改变 hash。对频繁切换的小条件，可声明条件 Pass 并让执行 predicate 决定，但其资源依赖仍保守存在。不要把所有条件都转成 runtime predicate，否则裁剪和别名收益消失。

## 12.5 确定性要求

- 所有 stable id 明确生成；
- dictionary 只用于查找，不决定输出顺序；
- worker setup 合并按 declaration token，不按完成时间；
- allocator 相同候选的 tie-break 固定；
- float size expression 在同一舍入模式下求值；
- capture/replay 固化 capability 与 dynamic inputs；
- debug name 不影响执行 hash，除非它是唯一 stable semantic key（不推荐）。

# 13. 多队列、异步计算与屏障

## 13.1 Queue 模型

逻辑 queue：Graphics、Compute、Copy。后端 capability 描述：

- 是否拥有独立 queue family / engine；
- timestamp 支持；
- sparse binding 支持；
- present 支持；
- queue family ownership transfer 成本；
- timeline semaphore/fence；
- concurrent resource sharing policy。

同一逻辑 queue 可映射到同一物理 queue；核心仍保留 batch 类型，用于诊断和将来设备变化。

## 13.2 异步计算选择

`EnableAsyncCompute(true)` 表示候选，不承诺实际异步。调度评分考虑：

```text
benefit = estimated overlap - queue transfer cost - cache/bandwidth contention
          - extra memory lifetime cost - extra descriptor/state cost
```

没有可靠 GPU cost model 时，默认尊重显式候选并提供 profile override。支持 per-platform rule table：特定 Pass、resource size、GPU family 可回落 graphics。

## 13.3 Early signal / late wait

对于 graphics producer Gp → compute C → graphics consumer Gc：

- signal 尽量放在满足 C 所有输入的最早 graphics batch 后；
- wait 尽量放在第一个真正消费 C 输出的 graphics batch 前；
- C 的其他输入可能来自上一帧 temporal queue，需要额外 wait；
- 如果 C 输出从未被 graphics 使用，不创建回合 wait；
- queue timeline value 由 batch 单调分配。

## 13.4 跨队列资源状态

API 无关状态拆成：

- resource access class；
- pipeline domain；
- queue owner；
- visibility scope；
- texture layout intent；
- discard/content-valid；
- alias generation。

这避免把 Vulkan stage/access/layout 或 D3D12 state 直接放入 core。

## 13.5 Split barrier

当后端支持并且距离足够长时，transition 可拆成 begin/end：

- begin 放在 producer 后，允许 transition 与无关工作重叠；
- end 放在 consumer 前；
- 范围和 before/after 必须完全匹配；
- 不能跨越 alias ownership 改变；
- capture 中显示 split pair id；
- D3D12 enhanced barrier 与 Vulkan release/acquire 映射分别处理。

## 13.6 Vulkan 映射

- 使用 synchronization2：`VkDependencyInfo`、`VkImageMemoryBarrier2`、`VkBufferMemoryBarrier2`；
- queue timeline 使用 timeline semaphore；
- 不同 queue family 时生成 release/acquire 和 family indices；
- dynamic rendering 为默认 raster 编码，合并组可选择传统 render pass/subpass；
- image layout 按 subresource range；
- buffer barrier 按 offset/size；
- alias memory 需要适当 memory dependency 和新资源初始化规则；
- sparse binding 通过 `vkQueueBindSparse` 纳入 timeline；
- validation layer 与 synchronization validation 必须开启测试。

## 13.7 D3D12 映射

- 优先 enhanced barriers；不支持时提供 resource-state fallback；
- texture barrier range 映射 mip/array/plane；
- buffer range 在 enhanced barrier 中表达，resource-state fallback 路径可能保守扩大；
- graphics/compute/copy queue 使用 fence；
- placed resources + heap aliasing barrier；
- split barrier 映射 BEGIN_ONLY/END_ONLY 或 enhanced sync；
- descriptor heap rollover 与 command list boundary 协调；
- D3D12 debug layer 与 GPU-based validation 进入 conformance。

## 13.8 UAV 与执行依赖

状态相同不表示无 barrier。连续 UAV write/read-write 若存在内存依赖，生成 UAV/memory barrier。若范围不相交且后端能证明，可省略；保守模式可扩大到整 resource。诊断必须区分 state transition 与 execution-only barrier。

## 13.9 Present 与多窗口

每个 backbuffer import 带 swapchain id、image index、acquire wait 和 present queue。多窗口/编辑器 viewport 产生多个 present 根。一个窗口最小化不应阻塞其他窗口；out-of-date 只失效对应 swapchain generation。

# 14. 瞬态内存、驻留、描述符与 bindless

## 14.1 资源类别

| 类别 | 物理所有者 | 可别名 | 跨帧 |
|---|---|---:|---:|
| Transient | frame allocator/cache | 是 | 否 |
| Temporal | temporal registry | 默认否；family 内受控复用 | 是 |
| Persistent | RenderWorld cache/feature | 仅显式允许 | 是 |
| External | 调用方/SDK | 否 | 由外部决定 |
| Sparse | residency manager | tile 级物理复用 | 是/否 |

Temporal 不进入普通 transient alias pool，因为其历史窗口和在途 completion 跨图。旧 generation 可在 completion 后释放，而不是与当前帧临时对象立即别名。

## 14.2 兼容 key

Texture key 至少包含：dimension、extent class、mip/layer、sample count、format compatibility class、tiling、usage、memory class、alignment、sparse/memoryless/export flags。Buffer key 包含 size class、alignment、usage、device address、AS/indirect/counter flags、memory class。

不要用完整描述作为唯一 key；允许大对象放入可容纳小对象的区间，但必须满足 alignment 和 backend compatibility。

## 14.3 别名证明

两个 logical allocations A/B 可共享物理区间当且仅当：

1. descriptor/memory compatibility；
2. batch DAG 可证明 `last(A) happens-before first(B)` 或相反；
3. 没有外部引用、bindless stable lifetime、readback 或 temporal pin；
4. alias boundary 插入执行与内存依赖；
5. B 的第一次有效操作不依赖旧内容；若不是全覆盖则先 clear/copy。

Debug clobber 模式在 alias acquire 后写入已知 pattern，帮助发现未声明读取。

## 14.4 分配算法

推荐两层：

- logical placement：按 memory class 分组，按 size/align/first frontier 排序；
- physical heap：best-fit free blocks + alias chain；超大资源单独 heap；
- 先用 topological interval 快速筛选，再用 reachability cache 验证偏序；
- reachability 使用 batch DAG 的 bitset transitive closure（batch 数较小）或 interval labeling + DFS fallback；
- allocation 输出稳定 tie-break，保证 capture 可复现。

## 14.5 Residency

离散显存需要预算与驻留管理：

- 查询本地/non-local budget；
- persistent resource 按优先级 pin/evict；
- transient heap 随帧峰值缩放但有 hard cap；
- temporal family 报告槽成本，允许 quality manager 降低 history/分辨率；
- sparse tiles 单独预算；
- eviction/re-resident 形成 batch dependency；
- UMA 路径关闭无意义的迁移，但保留统一统计。

预算压力处理顺序：回收完成对象 → trim caches → evict 可恢复资源 → 降低可配置质量 → 分配 overflow → 受控等待/失败。禁止无界增长。

## 14.6 Descriptor 与 view cache

- RTV/DSV/SRV/UAV 以 physical generation + normalized view key 缓存；
- transient descriptors 从每 FrameContext 的 arena 分配；
- worker 拥有局部分配块，避免全局锁；
- heap rollover 只能发生在编译器可见的 command list boundary；
- descriptor 在最后引用 submission 完成前不能回收；
- temporal 迁移导致 generation 改变时旧 view cache 延迟销毁。

## 14.7 Bindless

稳定 bindless index 由 RenderWorld registry 分配：

```text
index -> { resource stable id, physical generation, view key, descriptor generation }
```

shader 侧可选 generation table 检测 stale index。资源重建时优先原位更新 descriptor；若平台不允许，分配新 slot 并在 frame boundary 原子切换。transient resource 默认不获得稳定 bindless index，除非声明 frame-local bindless 并限制生命周期。

## 14.8 Memoryless / tile memory

移动 GPU 支持 memoryless attachment 时：

- 只有 attachment-only、无后续 sample/copy 的资源可选；
- fusion group 内 load/store 推导为 DontCare；
- 一旦需要跨 native pass 读取，必须 materialize；
- capture/debug 可强制 materialize，行为仍保持等价；
- memoryless 与 temporal、external、readback 互斥。

# 15. Raster、Resolve、Mip 与原生 Pass 合并

## 15.1 Attachment 完整语义

每个 attachment 记录：view range、load action、store action、clear value、read-only depth/stencil、resolve target/view/mode、sample count、view mask、VRS image。编译器验证：

- 同一 subresource 不能同时作为冲突 attachment/UAV，除非明确 feedback loop capability；
- color attachments 尺寸/sample/view count 一致；
- depth/stencil format 与 aspect 合法；
- Load 要求已有有效 producer；
- DontCare/Clear 可消除内容依赖但保留 hazard；
- Store 可根据后续消费者自动降为 DontCare；
- 最后一位消费者在 fusion group 内时可保持 tile-local。

## 15.2 Load/store 推导

用户可给明确值，也允许 `Auto`（内部 enum，不必公开）：

- 第一次使用且全覆盖 → DontCare 或 Clear；
- 需要旧内容 → Load；
- 无后续消费者且非 external/temporal → DontCare；
- 后续 sample/copy/present → Store；
- resolve 后 MSAA source 无消费者 → source DontCare，resolve target Store。

诊断显示“用户指定”或“编译器推导”以及理由。

## 15.3 MSAA 与 depth resolve

- color resolve source/destination 各有独立 range；
- resolve mode Average/Min/Max/SampleZero 按 format/capability 验证；
- depth/stencil resolve 可分别选择 mode；
- 自动 resolve 只在单采样消费者首次出现且不存在更早显式 resolve 时插入；
- resolve Pass 是图节点，可异步/融合取决于后端；
- temporal history 默认保存 resolved 单采样结果，除非算法明确需要 MSAA history。

## 15.4 自动 mip 生成

`TextureFlags` 或内部需求标记资源允许生成 mip。编译器在发现读取一个无 producer 的 mip 且较高层有有效 producer 时插入 mip generation chain。规则：

- format 必须可 filter/storage/copy；
- depth、normal、roughness 等可能需要自定义 reduction，不用通用 box；
- 每级输出是独立 subresource producer；
- 可在 compute queue，允许与不相关 graphics 重叠；
- 如果用户已显式写某一级，不重复生成；
- capture 显示自动注入来源。

## 15.5 Native render-pass fusion

合并条件：

- 同一 graphics queue 且拓扑相邻或可安全重排；
- attachment physical object/range/sample/view mask 兼容；
- 中间 attachment 仅在组内作为 input attachment/tile read；
- 无要求结束 rendering 的命令、global state、timestamp 限制；
- UAV/feedback loop 符合平台能力；
- load/store 可映射；
- VRS/foveated 状态兼容。

合并收益：减少 layout transition、保留 tile memory、降低 load/store。代价：可能限制并行记录或 async overlap。调度器用成本模型决定，且提供 `NeverMerge` 内部标志和诊断。

## 15.6 Input attachment 与 tile read

Builder 的 `SetInputAttachment` 声明 attachment-local read。后端映射：

- Vulkan subpass input 或 dynamic rendering local read capability；
- D3D12 使用 SRV/ROV/其他等价路径，无法 tile-local 时 materialize；
- 移动平台可映射 framebuffer fetch；
- 不支持时自动拆分 Pass 并 Store/Load，结果一致但性能不同。

## 15.7 Multiview / XR

`SetViewCount` 与 view mask 影响 attachment layer 解释、pipeline state 和 fusion。每个 view 可共享或独立 temporal family；眼间共享资源必须显式声明 array layer 范围。foveated/VRS state 是 Pass 状态的一部分，不能由全局隐式修改。

# 16. 3A 能力完整性

本章列出通常在 3A Render Graph 或其紧邻运行时中出现、且本方案必须体现并实现的能力。

## 16.1 GPU-driven 与 indirect

- indirect arguments、count buffer、predication 是独立 `ResourceAccess`；
- compute culling 写 arguments/count，graphics draw indirect 读；
- range 跟踪允许多个 view/mesh cluster 占同一大 buffer；
- ExecuteIndirect/DrawIndirectCount 等命令在 Graphics CommandBuffer；
- command signature/pipeline compatibility 在 Pass setup 验证；
- GPU-generated dispatch dimensions 也作为 indirect read；
- capture 记录 arguments buffer range 和 producer。

## 16.2 Ray tracing acceleration structures

图模型包含：

- BLAS/TLAS logical handle；
- geometry/input buffer reads；
- AS storage write/read；
- scratch buffer write；
- build、update/refit、copy、compact、serialize/deserialize；
- post-build compacted size query；
- TLAS instance buffer 与 BLAS dependencies；
- async compute build 与 graphics/ray dispatch wait；
- device address stability 和 residency pin。

AS 不应伪装成普通 buffer，因为 build/read barrier、compaction 和 lifetime 特殊。API 契约提供 import 与 read/write handle；具体 create/build 命令属于 Graphics 与内部 feature helper，但依赖进入同一图。

## 16.3 Sparse / reserved resources 与虚拟纹理

- sparse image/buffer 拥有逻辑完整尺寸与 tile mapping；
- tile pool allocation、map/unmap、page table update 形成明确 Pass/batch；
- sparse binding queue 的 signal 被后续 sample 等待；
- feedback/readback 决定下一帧 residency，不绕过 RenderWorld；
- page table 自身是 temporal/persistent resource；
- tile eviction 受 completion 与历史引用保护；
- capture 包含 tile map 与 budget；
- 不支持 sparse 的平台使用普通 atlas fallback，但 API 语义不变。

## 16.4 VRS 与 foveated rasterization

- shading-rate image 作为 attachment read；
- fragment size 和 combiner 是 Pass state；
- VRS image tile size/format 由 capability 验证；
- eye-tracked foveation 输入属于 RenderWorld 提取数据；
- multiview 下每 eye layer 独立或共享；
- 不支持硬件 VRS 时 fallback 为 dynamic resolution/compute upsample 或 1×1，不改变资源依赖。

## 16.5 Upload、Readback 与 Query

Upload：

- persistent mapped upload ring；
- 每分配记录 offset/size/completion；
- copy Pass 读取 upload range、写目标 range；
- 大上传可走 dedicated staging；
- non-coherent memory flush 对齐；
- ring wrap 受 completion 保护。

Readback：

- 图内 copy 到 readback buffer；
- readback request 是副作用根；
- callback 在 completion 后投递，不在渲染线程阻塞等待；
- request 包含 frame/submission/resource range；
- 取消只取消 callback，不提前回收 GPU memory。

Query：

- timestamp、occlusion、pipeline statistics；
- query pool 按 FrameContext 分配；
- begin/end 必须匹配并受 fusion 限制；
- resolve query data 是显式内部 Pass；
- GPU timestamp 跨 queue 校准；
- profiler 使用采样策略，关闭时不污染批次。

## 16.6 外部互操作

支持：swapchain、video decoder/encoder、DLSS/FSR/XeSS、streaming SDK、editor preview、platform compositor、external Graphics。每次 import 要求：

- physical generation；
- range 初始 access/state；
- queue owner；
- wait token；
- final access/queue；
- signal token；
- 是否允许图内 alias/rename（默认否）；
- 外部生命周期至少覆盖 completion。

## 16.7 Dynamic resolution 与尺寸表达式

TextureDesc 支持：

- explicit width/height/depth；
- reference size × scale；
- relative to another texture；
- alignment、round up/down、minimum/maximum；
- array/mip/sample 独立；
- max allocation + active valid region；
- dynamic resolution generation。

尺寸表达式在编译前求值并进入 descriptor generation。相对依赖必须无环；错误输出完整尺寸依赖链。

## 16.8 Pipeline/Shader readiness

图编译不负责耗时 shader 编译，但必须处理 pipeline 未就绪：

- blocking、skip、fallback pipeline、previous generation 四种 policy；
- policy 在 setup 时固定，worker 不临时改图；
- fallback 的资源访问必须与正式 Pass 契约兼容；
- hot reload 提升 pipeline layout generation，触发 graph cache 失效；
- PSO cache 与 shader library 有独立 telemetry。

## 16.9 Global state escape hatch

`AllowGlobalStateModification(true)` 适用于 debug overlay、external SDK 或无法描述的原生命令。代价：

- Pass 不可裁剪；
- 前后插入 conservative barrier；
- 不可并行记录或 fusion；
- descriptor/global state cache 失效；
- capture 标红；
- CI 可限制 shipping build 中的数量。

## 16.10 Capture/replay 与 GPU crash

Capture 固化：图声明、动态尺寸、capability、pipeline ids、external metadata、temporal slot mapping、batch、barrier、allocation、可选资源快照。Replay 可在 Null/Vulkan/D3D12 运行并比较编译结果。

GPU crash breadcrumbs：每个 Pass/batch 写 marker id；CPU 保存最近提交、资源 generation、barrier 和 descriptor 映射；DRED/Vulkan device fault 可关联回图节点。崩溃报告不能只给原生命令列表。

## 16.11 Production 运维能力

- per-feature GPU/CPU budget；
- graph diff（两个 capture 的 Pass/边/内存变化）；
- quality scaling hook；
- memory pressure event；
- shader/pipeline cache warmup；
- editor 单步执行或 disable Pass；
- no-alias、single-queue、no-fusion、synchronous RenderWorld 调试开关；
- deterministic seed 和 capture id；
- runtime feature capability report。

# 17. Vulkan 1.3 / D3D12 / Null 后端

## 17.1 Backend contract

Core 输出：

```text
ResourceCreateIntent
PhysicalAllocationIntent
ViewCreateIntent
BarrierIntent
CommandBatch
QueueWait / QueueSignal
RecordRange
PresentIntent
ReadbackIntent
```

Backend 返回：physical handles、descriptor handles、actual memory requirements、completion tokens、capability/fallback reason。Core 不含 `Vk*`、`D3D12_*` 枚举。

## 17.2 Null 后端

Null 后端不是“简化路径”，而是可执行规范模型：

- 模拟 per-range state machine；
- 验证所有 barrier intent；
- 模拟 queue timeline 与 batch DAG；
- 模拟 alias memory 并用 pattern clobber；
- 模拟 temporal slot 和 completion 延迟；
- 可注入随机 queue delay、device loss、budget pressure；
- 输出 deterministic JSON golden；
- 支持参考 interpreter 执行简单 copy/clear/hash，验证内容依赖。

## 17.3 Vulkan 1.3

必须覆盖：

- instance/device/queue selection；
- swapchain 多窗口与 resize；
- dynamic rendering、render pass/subpass fallback；
- synchronization2、timeline semaphore；
- memory allocator、dedicated allocation、alias binding；
- descriptor set/pool 或 descriptor buffer capability；
- buffer device address；
- ray tracing pipeline/AS；
- sparse binding；
- VRS fragment shading rate；
- calibrated timestamps；
- device fault/validation markers；
- pipeline cache 持久化。

## 17.4 D3D12

必须覆盖：

- graphics/compute/copy queues 与 fences；
- swapchain、多窗口、resize；
- enhanced barriers + resource-state fallback；
- committed/placed/reserved resources；
- heap tiers 与 alias barrier；
- RTV/DSV/CBV_SRV_UAV/sampler descriptor heaps；
- ExecuteIndirect、predication；
- DXR BLAS/TLAS、compaction；
- tiled resources；
- VRS；
- timestamp/readback；
- DRED、breadcrumbs、GPU-based validation；
- pipeline library。

## 17.5 能力协商

`BackendCapabilities` 只读快照进入 graph hash：

- subresource barrier granularity；
- independent depth/stencil；
- native render-pass/local read；
- async queue topology；
- timeline support；
- sparse tier；
- RT tier；
- VRS tier；
- descriptor indexing/bindless；
- memory budget/residency；
- enhanced barrier；
- max attachments/views/samples。

同一 feature 在能力不足时必须有明确 fallback 或清晰 compile error。fallback 也要通过 conformance，不允许 silent no-op。

## 17.6 后端一致性

同一 capture 在三个后端比较：

- live Pass 集；
- 范围 producer 关系；
- logical batch DAG；
- final content hash；
- temporal validity；
- memory safety；
- backend-specific barrier 可不同，但必须满足同一 abstract intent；
- unsupported optimization 可回落，但不能改变结果。

# 18. C# / .NET 10 工程实现

## 18.1 项目配置

```xml
<PropertyGroup>
  <TargetFramework>net10.0</TargetFramework>
  <LangVersion>14.0</LangVersion>
  <Nullable>enable</Nullable>
  <ImplicitUsings>disable</ImplicitUsings>
  <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
  <ServerGarbageCollection>true</ServerGarbageCollection>
  <Deterministic>true</Deterministic>
</PropertyGroup>
```

Core 尽量 safe C#；backend interop、native arrays、mapped memory 可用 unsafe，但封装在小范围并有 bounds/lifetime tests。

## 18.2 热路径内存

禁止热路径 LINQ、闭包、每 Pass `List<T>`、字符串拼接和反射。使用：

- `ArrayPool<T>` / 自研 slab pool；
- `ValueList<T>` + `Span<T>`；
- per-frame bump arena；
- struct handle/record；
- interned name id，字符串仅诊断；
- static lambda + generated thunk；
- thread-local compiler workspace；
- pooled bitset、interval vector、reader list；
- capacity high-watermark，下一帧复用。

稳定场景目标为构图、编译、记录调度 0 B managed allocation。

## 18.3 数据布局

- Pass、Resource、Access 使用 SoA 或紧凑 AoS，benchmark 决定；
- index 替代对象引用，利于序列化；
- range 使用 16/32-bit 字段，避免过大 struct；
- `ResourceAccess` 64-bit flags；
- queue/batch/pass index 32-bit；
- debug source location 存独立表，Release 可裁剪；
- `Compiled` 一次性连续分配，worker 只读。

## 18.4 Builder 形态

真实实现建议：

```csharp
public ref struct RenderGraphBuilder
{
    private ref GraphRecording _recording;
    private readonly int _passIndex;
    private bool _disposed;
}
```

`ref struct` 防止逃逸到 heap/async；Dispose 完成 setup。若需要 interface/泛型抽象，可由 source generator 生成静态调用，不把 Builder 装箱。

## 18.5 PassData source generator

Generator 读取特性或约定字段：

```csharp
partial sealed class SsaoPassData
{
    public TextureHandle Depth;
    public TextureHandle Normals;
    public TextureHandle Output;
}
```

生成：

- stable pass type id；
- setup binding helper；
- execution thunk；
- source location；
- capture serializer；
- AOT registration；
- API usage validation。

不使用运行时反射扫描。Generator 输出也进入 naming audit。

## 18.6 并行 setup、compile、record

Setup：Feature 可并行构建 thread-local declaration chunks，最终按预分配 declaration token 合并。互相依赖的 Feature 通过 job dependency 或 blackboard future 明确排序。

Compile：range normalization、resource-local dependency building、descriptor requirement query 可并行；拓扑/culling/allocator 的关键合并部分保持确定性。

Record：Compiled 输出 `RecordRange`；每 worker 有 command allocator/pool、descriptor block、scratch arena。global-state、query/fusion 限制的 Pass 标记串行。

## 18.7 异常与取消

worker 回调异常：

- 记录 Pass/source location；
- 取消尚未开始的 record ranges；
- 已记录但未提交的 command lists 丢弃；
- 不更新 temporal final state；
- 回收 frame-local resources；
- RenderWorld 仍按关闭协议归还；
- shipping 可配置 fatal 或 feature fallback。

## 18.8 NativeAOT

目标支持 NativeAOT：

- 无运行时生成 IL；
- generator 提供类型表；
- P/Invoke 使用 LibraryImport；
- JSON capture 使用 source-generated serialization；
- backend plugin 若需动态加载，提供静态链接配置；
- AOT sample 进入 CI，而不是发布前才验证。

# 19. 验证、诊断、捕获与故障恢复

## 19.1 Validation 层

错误至少包含：

- code、严重级别；
- Pass 名/id/source file/line；
- resource 名/stable id/version/generation；
- normalized range；
- expected/actual access、queue、state；
- 最短依赖链；
- 推荐修复。

覆盖：跨 graph handle、过期 epoch、range 超界、read-before-write、重叠冲突、history 非法写、未声明解析、错误 attachment、queue capability、external state 缺失、alias 不安全、环、migration 不支持、descriptor stale、bindless generation mismatch。

## 19.2 诊断数据

每个 capture 包含：

- Pass/资源版本 DAG；
- range producer map；
- temporal family/slot/generation；
- batch timeline 与 wait/signal；
- barrier 原因与合并前后数量；
- physical heap、offset、alias chain、residency；
- descriptor/bindless mapping；
- fusion group、load/store/resolve/mip injection；
- culling reason；
- actual queue/fallback reason；
- CPU setup/compile/record timing；
- GPU timestamp；
- source location 与 feature owner。

## 19.3 可回答性

工具必须直接回答：

- 为什么 Pass 被裁剪/未被裁剪？
- 为什么资源没有别名？
- 为什么 async compute 回落？
- 为什么这个 mip 有 barrier，而另一个没有？
- 为什么上一帧历史触发等待？
- 为什么发生 resample 而不是 copy？
- 哪个 Pass 延长了显存寿命？
- 哪个 external state 缺失？
- 哪个 bindless slot 指向旧 generation？

答案来自 Compiled 的 reason codes，不重新猜测。

## 19.4 Capture/replay

Capture 级别：

1. Structure：图、batch、barrier、allocation；
2. State：加 external/temporal metadata 与 pipeline ids；
3. Content：选择性保存 resource snapshots；
4. Crash：环形保存最近 N 帧结构 + 最近提交 breadcrumbs。

Replay 支持 deterministic compile diff；内容回放需处理受版权/隐私影响的纹理数据，可配置脱敏或 hash-only。

## 19.5 故障恢复

设备丢失恢复 journal 保存：

- persistent/temporal logical desc 与 stable id；
- asset source/generation；
- bindless stable slot；
- pipeline/shader keys；
- external resources 需要调用方重新 import；
- temporal history valid=false；
- pending readback/query 以错误完成；
- deferred release 队列按 device generation 丢弃原生句柄但保留逻辑释放。

恢复后第一个图必须自动插入必要 initialize，并在诊断中标记 recovery generation。

# 20. 一期任务清单与依赖关系

[[FIGURE:figures/task_graph.png]]

> T01～T20 只是工作分解编号。它们可以由不同小组并行执行；依赖表示合并和验证所需的先后关系。**所有任务均属于一期，只有全部验收通过才算完成。**

## T01 — 公开契约、命名证据与兼容门禁

**工作**：维护 `RenderGraph.Api.cs`；自动提取 public 标识符；GitHub code search 证据；禁用后缀；API diff/baseline；文档同步检查。
**依赖**：无。
**产物**：API assembly、evidence JSON、audit JSON、API compatibility report。
**验收**：证据缺失 0；禁用后缀 0；文档/API identifiers 一致；CI 中任何新增 public 名称无证据即失败。

## T02 — 核心表、句柄、arena 与确定性基础

**工作**：Resource/Pass/View/Access 表；64-bit handle；graph epoch；stable ids；source location；pool/arena；deterministic collections。
**依赖**：T01 契约。
**验收**：跨图/过期句柄测试；100 万 handle encode/decode；稳定 hash；稳态 0 B allocation 基线。

## T03 — Texture 子资源与 Buffer 区间版本编译

**工作**：normalize/intersect/subtract/coalesce；uniform/expanded/sparse state；buffer interval map；局部 producer overlay；RAW/WAR/WAW；physical rename policy。
**依赖**：T02。
**验收**：第 8、9 章全部单元和随机范围测试；与慢速 reference model 一致；局部写不污染无关范围。

## T04 — Temporal registry、跨帧边与自动迁移

**工作**：stable family、slot、history index、generation、completion、valid region、reset、迁移注入、旧 generation 延迟销毁。
**依赖**：T02、T03 的范围模型。
**验收**：多历史、多 in-flight、resize、camera cut、format/sample、device loss、partial subresource 测试。

## T05 — 图依赖、裁剪、排序、缓存与 Null 后端

**工作**：range edge、roots、culling、cycle、stable topo、Compiled、template cache、Null state interpreter。
**依赖**：T03、T04。
**验收**：10 万随机图；cycle 解释；cache/full compile 双运行一致；Null barrier/state 无错误。

## T06 — 多队列调度、timeline 与屏障编译

**工作**：queue capability、async window、early signal/late wait、batch DAG、abstract barrier、split barrier、fallback reason。
**依赖**：T03、T04、T05。
**验收**：跨队列 litmus；无路径资源不错误别名；历史跨 queue wait；single-queue fallback 一致。

## T07 — 瞬态分配、别名、驻留与预算

**工作**：memory requirements、compat key、batch happens-before、heap placement、alias barrier、residency、budget pressure、debug clobber。
**依赖**：T05、T06。
**验收**：属性测试证明每对 alias 有 happens-before；no-alias 内容一致；预算压力可控；无未初始化读取。

## T08 — Descriptor、view cache 与 bindless

**工作**：normalized view hash、descriptor arenas、worker blocks、heap rollover、stable bindless slot、generation table、延迟回收。
**依赖**：T02、T04、T07。
**验收**：resize/hot-reload 后无 stale descriptor；heap rollover 正确；bindless generation mismatch 被检测。

## T09 — Raster attachment、resolve、mip、fusion 与 tile memory

**工作**：load/store 推导、depth/stencil、MSAA/depth resolve、auto mip、input attachment、native fusion、memoryless、multiview/VRS state。
**依赖**：T03、T05、T06、T08。
**验收**：fusion on/off 图像一致；自动 resolve/mip producer 正确；移动 tile path 不产生多余 store/load。

## T10 — Vulkan 1.3 后端

**工作**：device/swapchain、sync2、timeline、dynamic rendering/render pass、allocator、descriptor、BDA、RT、sparse、VRS、queries、device fault。
**依赖**：T06～T09。
**验收**：validation 0 错误；全 conformance；多窗口/resize/device lost；GPU capture 可关联图节点。

## T11 — D3D12 后端

**工作**：queues/fence、enhanced barriers、placed/reserved resources、descriptor heaps、indirect、DXR、tiled resources、VRS、DRED。
**依赖**：T06～T09。
**验收**：debug layer/GPU validation 0 错误；与 Vulkan 同场景 hash 一致；Core 无 D3D12 业务特例。

## T12 — GPU-driven、RTAS、Sparse 与 VRS 功能集成

**工作**：indirect/count/predication；BLAS/TLAS/scratch/compaction；virtual texture tile/page table；shading-rate image/foveation；fallback。
**依赖**：T08～T11。
**验收**：GPU-driven sample、RT sample、sparse streaming sample、VRS/multiview sample 在支持后端运行；fallback 结果正确。

## T13 — Upload、readback、query 与外部 SDK 边界

**工作**：upload ring、readback pool、query pool/resolve、external wait/signal、video/upscaler interop、multi-swapchain。
**依赖**：T06、T08、T10、T11。
**验收**：ring wrap、异步 callback、timestamp calibration、external ownership、取消/关闭无泄漏。

## T14 — Parallel setup/compile/record 与 source generator

**工作**：thread-local chunks、deterministic merge、parallel compile jobs、RecordRange、worker resources、PassData generator、AOT registration。
**依赖**：T02、T05、T08。
**验收**：串并行结果 bit-identical；无全局热锁；异常取消安全；NativeAOT sample 通过。

## T15 — RenderWorld 所有权流水

**工作**：双向 Channel、Extract、schedule、同步适配、fault channel、shutdown、device recovery、temporal ownership。
**依赖**：T04、T05、T13、T14。
**验收**：Simulation N+1 / Render N 重叠；压力注入无死锁/双重所有权；线程亲和析构正确。

## T16 — 诊断、viewer、capture/replay 与 crash breadcrumbs

**工作**：DOT/JSON、timeline、memory map、reason codes、graph diff、content capture、replay、DRED/device fault 关联。
**依赖**：T05～T15 的 compiled/runtime data。
**验收**：可回答第 19.3 节全部问题；capture replay 编译 hash 一致；crash report 映射到 Pass/resource/range。

## T17 — 单元、属性、随机、golden 与后端一致性测试

**工作**：reference model、random DAG/range/queue/history；golden；validation layer；stress/fault injection。
**依赖**：贯穿 T02～T16。
**验收**：第 21 章矩阵全部绿色；随机种子可复现；失败最小化器输出最小图。

## T18 — 基准、预算与性能回归门禁

**工作**：build/compile/range/barrier/allocator/record/submit/RenderWorld benchmark；managed allocation；GPU overlap/memory。
**依赖**：T05～T15。
**验收**：第 22 章预算；CI trend；超阈值阻止合并并附差异 capture。

## T19 — 3A 综合样例与引擎集成

**工作**：deferred renderer、shadows、Hi-Z、async SSAO、SSR、TAA/TSR history、bloom、GPU-driven、RT reflection、virtual texture、UI/present。
**依赖**：T09～T16。
**验收**：功能开关组合、动态分辨率、多窗口、热重载、device recovery；Vulkan/D3D12 画面 hash/容差一致。

## T20 — 文档、发布包与门禁汇总

**工作**：技术文档、API、证据、任务追踪、测试与基准结果、migration guide、ADR、包内容校验。
**依赖**：T01～T19。
**验收**：ZIP manifest hash；DOCX 渲染 QA；所有链接/文件可打开；一期完成定义无缺项。

## 20.1 并行建议

可并行的主工作流：

- Core：T02/T03/T05；
- Temporal：T04；
- Scheduler/Memory：T06/T07；
- Binding/Raster：T08/T09；
- Backends：T10/T11；
- Advanced features：T12/T13；
- Runtime/tooling：T14/T15/T16；
- QA/performance/integration：T17/T18/T19；
- T01 与 T20 全程持续。

依赖不改变交付范围。例如 Vulkan 后端可在 abstract barrier schema 稳定后并行编码，但最终必须等待 T03/T04 的范围与 temporal conformance 才能完成验收。

# 21. 测试与一致性矩阵

## 21.1 单元测试

| 领域 | 核心用例 |
|---|---|
| Handle | epoch、kind、version、null、overflow |
| Range | normalize、边界、空、交集、相减、合并、cube/plane |
| Version | 局部覆盖、继承、分支、read-modify-write、discard |
| Buffer | interval split/merge、counter、indirect、host |
| Temporal | slot 解析、history index、completion、reset、generation |
| Culling | present、external、readback、temporal root、migration 裁剪 |
| Queue | fallback、frontier、early/late、ownership |
| Memory | compatibility、happens-before、alias acquire、clobber |
| Raster | load/store、resolve、mip、fusion、input attachment |
| Descriptor | cache key、generation、rollover、bindless stale |

## 21.2 属性与随机测试

生成维度：

- 1～500 Pass；
- 1～1000 resources；
- texture 1～16 mip、1～128 layer、1～3 plane；
- random overlapping ranges；
- random queue topology；
- random history 0～8、in-flight 1～4；
- random descriptor changes、reset、device loss；
- random budget pressure；
- random feature capability/fallback。

慢速 reference model 逐 cell/逐 byte interval 模拟 producer 和 state。优化编译器结果必须与其一致。失败图使用 delta debugging 自动最小化，并保存 seed/capture。

## 21.3 Barrier litmus

至少包括：

- graphics RT write → compute sample；
- compute UAV write → graphics indirect read；
- copy write → AS build read；
- AS build → ray dispatch；
- sparse bind → texture sample；
- previous-frame compute write → current graphics read；
- depth write → depth read/stencil write；
- different mip no dependency；
- same state UAV execution dependency；
- alias old → alias new first partial write。

## 21.4 Temporal 场景

- GPU 延迟 0～6 帧随机变化；
- history count 小于/等于/大于 in-flight；
- overflow slot 与 hard budget wait；
- resize oscillation；
- camera cut while previous frame in flight；
- partial mip history；
- multi-view separate/shared family；
- hot reload algorithm version；
- device recovery。

## 21.5 Raster 与 3A 场景

- deferred GBuffer + depth pyramid；
- async SSAO/SSR；
- shadow atlas layer update；
- bloom mip chain；
- TAA/TSR + dynamic resolution；
- MSAA forward + resolve；
- tile renderer fusion；
- GPU-driven meshlet culling；
- RTAS update + ray reflection；
- virtual texture streaming；
- VRS/foveated multiview；
- video/upscaler external interop。

## 21.6 后端一致性

比较策略：

- integer/hash buffers bit exact；
- float images 使用明确 epsilon/ULP 与 format tolerance；
- logical graph/batch/producer map exact；
- physical allocation可不同但必须安全；
- barrier native encoding 可不同但 abstract intent coverage exact；
- timestamp 只验证偏序，不比较绝对值。

## 21.7 RenderWorld 压力

随机暂停主线程、渲染线程、worker 和 GPU completion；注入异常、取消、热重载、窗口创建销毁、设备丢失。断言：

- 任一时刻 RenderWorld 可变 owner 至多一个；
- 通道每方向最多一个对象；
- no use-after-send；
- pending release 最终完成；
- shutdown 有界且不死锁；
- temporal registry 不被主线程写；
- fault 后不提交半编译图。

# 22. 性能预算

以下为工程验收预算，必须在固定硬件、固定图和固定构建配置下记录 p50/p95/p99；不是对所有项目的普遍事实承诺。

| 指标 | 一期预算 |
|---|---:|
| 500 Pass / 1000 resource / 5000 access 完整编译 p95 | ≤ 1.5 ms |
| 同图模板缓存命中绑定 p95 | ≤ 0.35 ms |
| 10 万 texture range normalize | ≤ 1.0 ms |
| 10 万 buffer interval 更新 | ≤ 1.5 ms |
| 5000 barrier intent 合并 | ≤ 0.35 ms |
| 2000 transient allocation placement | ≤ 0.50 ms |
| 稳定场景 managed allocation | 0 B/frame |
| RenderWorld handoff 开销 p95 | ≤ 50 µs，不含等待 |
| worker 调度全局锁 | 0 个热路径全局锁 |
| capture 关闭 CPU 开销 | ≤ 1% compile+record |
| no-alias → alias 显存收益 | 报告实际值；典型样例目标 ≥ 25% |
| async compute | 必须报告 overlap 与额外显存；无固定必增益假设 |

## 22.1 Benchmark 拆分

- API build；
- range normalization；
- dependency construction；
- culling/topo；
- temporal resolution/migration；
- scheduler/batch；
- allocator/residency；
- barrier merge；
- descriptor resolution；
- parallel record dispatch；
- backend command encoding；
- RenderWorld handoff；
- capture serialization。

## 22.2 数据集

至少维护：SmallForward、Deferred500、AsyncHeavy、MipLayerStress、TemporalStress、VirtualTexture、RayTracing、EditorMultiView、WorstCaseIntervals。每个数据集有 frozen capture 与 expected metrics range。

## 22.3 回归门禁

- p95 超预算 10% 或分配从 0 B 变非零即失败；
- 编译 hash 或 batch 变化需附 graph diff；
- 显存峰值增加需附 lifetime/alias diff；
- async overlap 下降需附 queue timeline；
- benchmark 结果记录 runtime、OS、driver、GPU、CPU、build commit。

# 23. 仓库结构与模块边界

```text
src/
  RenderGraph.Api/
  RenderGraph.Core/
  RenderGraph.Compiler/
  RenderGraph.Runtime/
  RenderGraph.Memory/
  RenderGraph.Temporal/
  RenderGraph.Diagnostics/
  RenderGraph.Generators/
  RenderGraph.Backend.Null/
  RenderGraph.Backend.Vulkan/
  RenderGraph.Backend.D3D12/
  RenderWorld.Runtime/
tests/
  RenderGraph.UnitTests/
  RenderGraph.PropertyTests/
  RenderGraph.RandomTests/
  RenderGraph.ConformanceTests/
  RenderGraph.BackendTests/
  RenderWorld.StressTests/
  RenderGraph.AotTests/
samples/
  DeferredTemporalSample/
  AsyncComputeSample/
  GpuDrivenSample/
  RayTracingSample/
  VirtualTextureSample/
  MultiviewVrsSample/
benchmarks/
  SomeEngine.RenderGraph.Benchmarks/
tools/
  RenderGraph.Viewer/
  RenderGraph.Replay/
  RenderGraph.CaptureDiff/
eng/
  ApiEvidence/
  NamingAudit/
  TestSeeds/
  Baselines/
```

依赖规则：

- Api 不依赖 backend；
- Core 不依赖 Runtime/Graphics；
- Compiler 只依赖 Core 和 capability contracts；
- Temporal 依赖 Core 抽象，不依赖具体 API；
- Backend 实现 Runtime contracts；
- Diagnostics 读取 immutable data；
- Generator 不被 Runtime 引用；
- Samples 不能绕过 public API 使用 internal，除 backend conformance harness。

# 24. 风险与决策记录

## 24.1 风险

| 风险 | 后果 | 控制措施 |
|---|---|---|
| 子资源状态爆炸 | 编译时间/内存上升 | uniform fast path、稀疏/密集切换、range coalesce、benchmark 阈值 |
| 局部版本错误 | 隐蔽图像损坏 | reference model、random overlap、debug clobber、producer visualization |
| temporal 槽覆盖过早 | 跨帧随机闪烁 | completion-based reuse、slot pin、压力测试、overflow telemetry |
| 自动迁移代价过高 | GPU 尖峰 | migration 可裁剪、valid region、policy、diagnostic、quality hook |
| async 扩大显存 | OOM/抖动 | memory-aware scheduler、queue fallback、峰值预算 |
| alias 偏序判断错误 | 随机 corruption | batch happens-before proof、no-alias mode、property tests |
| descriptor stale | 崩溃/错误采样 | physical generation、stable bindless generation、延迟回收 |
| 两后端语义漂移 | 平台差异 | abstract intent、同 capture conformance、debug layers |
| C# GC/闭包 | CPU frame spike | arena、static lambda、generator、allocation gate |
| RenderWorld deadlock | 无法退出 | capacity 1 protocol、fault channel、shutdown state machine、stress |
| 功能标志只有表面实现 | 技术债 | 每项必须有 executable path、fallback、validation、test |

## 24.2 ADR-001：一期全量范围

**决定**：本文列出的核心与 3A 能力全部属于一期；任务只用于工作分配。
**理由**：子资源、temporal、queue、memory、raster 和后端彼此改变基础数据模型，拆成产品阶段会造成二次重写和错误边界。

## 24.3 ADR-002：UE5 子资源 + RPS temporal

**决定**：子资源以 UE5 RDG 为主参考，跨帧以 RPS temporal 为主参考。
**理由**：两者分别提供最明确的 per-subresource state 和 frame-index/temporal slice 语义；强行只选一个会丢失关键能力。

## 24.4 ADR-003：资源与视图分离

**决定**：handle 表示逻辑版本，view desc 表示访问范围。
**理由**：支持 mip/layer/plane、格式 reinterpret、attachment/descriptor cache、history slice。

## 24.5 ADR-004：范围覆盖版本

**决定**：局部写通过 producer map overlay，未覆盖范围继承旧版本。
**理由**：整资源单 producer 无法正确表达局部更新。

## 24.6 ADR-005：迁移 Pass 图内化

**决定**：copy/resample/resolve/convert/clear 迁移作为内部 Pass。
**理由**：必须参与依赖、裁剪、queue、barrier、timestamp、capture 和错误诊断。

## 24.7 ADR-006：偏序安全别名

**决定**：别名依据 batch DAG happens-before。
**理由**：多队列下线性序号不能证明不并发。

## 24.8 ADR-007：RenderWorld 整体所有权移交

**决定**：容量 1 双向 Channel 传递整个 RenderWorld。
**理由**：单写者、天然背压、清晰关闭与线程亲和销毁。[B1]

## 24.9 ADR-008：Vulkan 与 D3D12 同期验收

**决定**：两后端共同作为一期完成条件。
**理由**：第二后端能及时暴露 core 抽象偏置；不能等接口冻结后才发现状态/描述符/内存模型不通用。

## 24.10 ADR-009：诊断与执行同源

**决定**：viewer/capture 序列化 Compiled，不建立第二套推导。
**理由**：生产问题需要工具解释真实决策，而非近似重建。

# 25. 需求追踪与交付门禁

## 25.1 用户约束追踪

| 用户约束 | 设计位置 | 任务 | 验收 |
|---|---|---|---|
| C# / .NET 10 | 第 18 章 | T01/T14 | net10.0、C#14、AOT sample |
| 异步参考 Bevy RenderWorld | 第 5 章 | T15 | capacity-1、压力与关闭测试 |
| 任意 mip/layer | 第 8 章 | T03 | subresource random/reference tests |
| 跨帧自动迁移 | 第 10 章 | T04 | resize/format/sample/reset/device loss |
| 3A RG 完整能力 | 第 16 章 | T09/T12/T13/T19 | 综合样例与 fallback |
| API 名称 GitHub 证据 | 第 6 章 | T01 | missing evidence = 0 |
| 禁止特定后缀 | 第 6 章 | T01 | violations = 0 |
| 一期全部实现、非分期 | 第 1、20 章 | T01～T20 | 全部任务共同完成定义 |

## 25.2 ZIP 内容门禁

构建脚本必须验证：

- manifest 中所有文件存在；
- Markdown 与 DOCX 版本号一致；
- API 文件 hash 写入 evidence/audit metadata；
- evidence identifier set == API public identifier set；
- audit 0 violation；
- task matrix 覆盖所有 requirement id；
- DOCX 已渲染并逐页 QA；
- ZIP 可重新解压且 hashes 一致；
- 不包含临时 render PNG、缓存或私有 font 文件。

## 25.3 发布阻断项

任何一个以下条件阻断一期发布：

- 仍存在整资源-only fallback 但未标明平台限制；
- temporal 仍要求 feature 手工 import；
- migration 在图外执行；
- alias 无 happens-before 证明；
- Vulkan/D3D12 任一 conformance 未完成；
- public identifier 缺 GitHub 证据或命中禁用后缀；
- managed allocation/performance 超预算且无批准；
- DOCX 渲染存在裁切、乱码、表格越界。

# 26. API 契约附录

以下内容与配套 `RenderGraph.Api.cs` 同源。方法体仅用于使契约文件自包含，不代表后端实现。

```csharp
// Public contract draft for the .NET 10 render graph design.
// The file describes API shape only; backend bodies are intentionally omitted.
#nullable enable

using System;
using System.Numerics;

namespace Engine.Rendering;

public sealed class RenderGraph : IDisposable
{
    public TextureHandle CreateTexture(in TextureDesc desc) => default;
    public BufferHandle CreateBuffer(in BufferDesc desc) => default;

    public TextureHandle ImportTexture(Texture texture, in ImportResourceParams importParams) => default;
    public BufferHandle ImportBuffer(BufferHandle buffer, in ImportResourceParams importParams) => default;
    public TextureHandle ImportBackbuffer(Texture texture, in ImportResourceParams importParams) => default;
    public RayTracingAccelerationStructureHandle ImportRayTracingAccelerationStructure(
        RayTracingAccelerationStructure accelerationStructure,
        string name) => default;

    public RenderGraphBuilder AddPass<TData>(
        string name,
        QueueType queue,
        out TData data)
        where TData : class, new()
    {
        data = new TData();
        return default;
    }

    public void BeginRecording(in RenderGraphParameters parameters) { }
    public void EndRecordingAndExecute() { }
    public void EndFrame() { }
    public void Dispose() { }
}

public struct RenderGraphBuilder : IDisposable
{
    public TextureHandle ReadTexture(
        in TextureHandle input,
        in TextureViewDesc view,
        ResourceAccess access = ResourceAccess.ShaderRead) => input;

    public TextureHandle WriteTexture(
        in TextureHandle input,
        in TextureViewDesc view,
        ResourceAccess access = ResourceAccess.ShaderWrite,
        AccessFlags flags = AccessFlags.Write) => input;

    public TextureHandle ReadWriteTexture(
        in TextureHandle input,
        in TextureViewDesc view,
        ResourceAccess access = ResourceAccess.ShaderRead | ResourceAccess.ShaderWrite) => input;

    public BufferHandle ReadBuffer(
        in BufferHandle input,
        in BufferViewDesc view,
        ResourceAccess access = ResourceAccess.ShaderRead) => input;

    public BufferHandle WriteBuffer(
        in BufferHandle input,
        in BufferViewDesc view,
        ResourceAccess access = ResourceAccess.ShaderWrite,
        AccessFlags flags = AccessFlags.Write) => input;

    public BufferHandle ReadWriteBuffer(
        in BufferHandle input,
        in BufferViewDesc view,
        ResourceAccess access = ResourceAccess.ShaderRead | ResourceAccess.ShaderWrite) => input;

    public RayTracingAccelerationStructureHandle ReadRayTracingAccelerationStructure(
        in RayTracingAccelerationStructureHandle input) => input;

    public RayTracingAccelerationStructureHandle WriteRayTracingAccelerationStructure(
        in RayTracingAccelerationStructureHandle input) => input;

    public TextureHandle SetRenderAttachment(
        TextureHandle texture,
        int index,
        in RenderAttachmentDesc attachment,
        AccessFlags flags = AccessFlags.Write) => texture;

    public TextureHandle SetRenderAttachmentDepth(
        TextureHandle texture,
        in RenderAttachmentDesc attachment,
        AccessFlags flags = AccessFlags.ReadWrite) => texture;

    public void SetInputAttachment(
        TextureHandle texture,
        int index,
        in TextureViewDesc view) { }

    public TextureHandle SetRandomAccessAttachment(
        TextureHandle texture,
        int index,
        in TextureViewDesc view,
        AccessFlags flags = AccessFlags.ReadWrite) => texture;

    public BufferHandle SetRandomAccessAttachment(
        BufferHandle buffer,
        int index,
        in BufferViewDesc view,
        AccessFlags flags = AccessFlags.ReadWrite) => buffer;

    public void SetShadingRateImageAttachment(in TextureHandle texture) { }
    public void SetShadingRateFragmentSize(ShadingRateFragmentSize fragmentSize) { }
    public void SetShadingRateCombiner(
        ShadingRateCombinerStage stage,
        ShadingRateCombiner combiner) { }
    public void SetViewCount(int viewCount) { }

    public void EnableAsyncCompute(bool value) { }
    public void AllowPassCulling(bool value) { }
    public void AllowGlobalStateModification(bool value) { }
    public void EnableFoveatedRasterization(bool value) { }
    public void GenerateDebugData(bool value) { }

    public void SetRenderFunc<TData>(
        Action<TData, RenderGraphContext> renderFunc)
        where TData : class, new() { }

    public void Dispose() { }
}

public readonly struct TextureHandle
{
    private readonly ulong _value;
    public bool IsValid() => _value != 0;
}

public readonly struct BufferHandle
{
    private readonly ulong _value;
    public bool IsValid() => _value != 0;
}

public readonly struct RayTracingAccelerationStructureHandle
{
    private readonly ulong _value;
    public bool IsValid() => _value != 0;
}

public readonly struct TextureSubresourceRange
{
    public static TextureSubresourceRange All => default;

    public int MipIndex { get; init; }
    public int NumMips { get; init; }
    public int ArraySlice { get; init; }
    public int NumArraySlices { get; init; }
    public int PlaneSlice { get; init; }
    public int NumPlaneSlices { get; init; }
    public TextureAspect Aspect { get; init; }
}

public readonly struct BufferRange
{
    public static BufferRange All => new() { Offset = 0, Size = -1 };

    public long Offset { get; init; }
    public long Size { get; init; }
}

public readonly struct TextureViewDesc
{
    public TextureSubresourceRange Range { get; init; }
    public Format Format { get; init; }
    public TextureDimension Dimension { get; init; }
    public int HistoryIndex { get; init; }
}

public readonly struct BufferViewDesc
{
    public BufferRange Range { get; init; }
    public Format Format { get; init; }
    public int Stride { get; init; }
    public int HistoryIndex { get; init; }
}

public readonly struct RenderAttachmentDesc
{
    public TextureViewDesc View { get; init; }
    public RenderBufferLoadAction LoadAction { get; init; }
    public RenderBufferStoreAction StoreAction { get; init; }
    public ClearValue ClearValue { get; init; }
    public TextureHandle ResolveTexture { get; init; }
    public TextureViewDesc ResolveView { get; init; }
    public ResolveMode ResolveMode { get; init; }
}

public readonly struct TextureDesc
{
    public string Name { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public int Depth { get; init; }
    public int ArraySize { get; init; }
    public int MipCount { get; init; }
    public int SampleCount { get; init; }
    public int Alignment { get; init; }
    public int HistoryCount { get; init; }
    public Vector2 Scale { get; init; }
    public TextureHandle RelativeTo { get; init; }
    public Format Format { get; init; }
    public TextureDimension Dimension { get; init; }
    public TextureSizeMode SizeMode { get; init; }
    public TextureFlags Flags { get; init; }
    public ResizeMode ResizeMode { get; init; }
    public ClearValue ClearValue { get; init; }
}

public readonly struct BufferDesc
{
    public string Name { get; init; }
    public long Size { get; init; }
    public int Stride { get; init; }
    public int Alignment { get; init; }
    public int HistoryCount { get; init; }
    public BufferFlags Flags { get; init; }
    public ResizeMode ResizeMode { get; init; }
}

public readonly struct ImportResourceParams
{
    public ResourceAccess InitialAccess { get; init; }
    public ResourceAccess FinalAccess { get; init; }
    public QueueType Queue { get; init; }
    public ulong WaitValue { get; init; }
    public ulong SignalValue { get; init; }
    public bool PreserveContents { get; init; }
}

public readonly struct RenderGraphParameters
{
    public string ExecutionName { get; init; }
    public ulong FrameIndex { get; init; }
    public ulong GpuCompletedFrameIndex { get; init; }
    public bool ResetHistory { get; init; }
    public int MaxFramesInFlight { get; init; }
    public int ReferenceWidth { get; init; }
    public int ReferenceHeight { get; init; }
    public bool GenerateDebugData { get; init; }
}

public readonly struct ClearValue
{
    public Vector4 Color { get; init; }
    public float Depth { get; init; }
    public uint Stencil { get; init; }
}

[Flags]
public enum AccessFlags
{
    None = 0,
    Read = 1,
    Write = 2,
    ReadWrite = Read | Write,
    Discard = 4,
    WriteAll = Write | Discard,
}

[Flags]
public enum ResourceAccess : ulong
{
    None = 0,
    ShaderRead = 1UL << 0,
    ShaderWrite = 1UL << 1,
    RenderTarget = 1UL << 2,
    DepthRead = 1UL << 3,
    DepthWrite = 1UL << 4,
    CopySource = 1UL << 5,
    CopyDestination = 1UL << 6,
    ResolveSource = 1UL << 7,
    ResolveDestination = 1UL << 8,
    VertexBuffer = 1UL << 9,
    IndexBuffer = 1UL << 10,
    ConstantBuffer = 1UL << 11,
    IndirectArguments = 1UL << 12,
    Predication = 1UL << 13,
    AccelerationStructureRead = 1UL << 14,
    AccelerationStructureWrite = 1UL << 15,
    ShadingRate = 1UL << 16,
    InputAttachment = 1UL << 17,
    Present = 1UL << 18,
    HostRead = 1UL << 19,
    HostWrite = 1UL << 20,
    SparseBinding = 1UL << 21,
}

[Flags]
public enum TextureFlags
{
    None = 0,
    Persistent = 1 << 0,
    Memoryless = 1 << 1,
    Sparse = 1 << 2,
    Bindless = 1 << 3,
    Exportable = 1 << 4,
    Aliasable = 1 << 5,
}

[Flags]
public enum BufferFlags
{
    None = 0,
    Persistent = 1 << 0,
    Sparse = 1 << 1,
    Bindless = 1 << 2,
    Exportable = 1 << 3,
    Aliasable = 1 << 4,
    AccelerationStructure = 1 << 5,
    IndirectArguments = 1 << 6,
    Predication = 1 << 7,
    Counter = 1 << 8,
}

[Flags]
public enum TextureAspect
{
    None = 0,
    Color = 1 << 0,
    Depth = 1 << 1,
    Stencil = 1 << 2,
    Plane0 = 1 << 3,
    Plane1 = 1 << 4,
    Plane2 = 1 << 5,
}

public enum QueueType
{
    Graphics,
    Compute,
    Copy,
}

public enum TextureSizeMode
{
    Explicit,
    Scale,
    Relative,
}

public enum ResizeMode
{
    Discard,
    Copy,
    Resample,
    Clear,
}

public enum RenderBufferLoadAction
{
    Load,
    Clear,
    DontCare,
}

public enum RenderBufferStoreAction
{
    Store,
    DontCare,
    Resolve,
    StoreAndResolve,
}

public enum ResolveMode
{
    None,
    Average,
    Min,
    Max,
    SampleZero,
}

public enum ShadingRateFragmentSize
{
    FragmentSize1x1,
    FragmentSize1x2,
    FragmentSize2x1,
    FragmentSize2x2,
    FragmentSize2x4,
    FragmentSize4x2,
    FragmentSize4x4,
}

public enum ShadingRateCombinerStage
{
    Primitive,
    Fragment,
}

public enum ShadingRateCombiner
{
    Keep,
    Replace,
    Min,
    Max,
    Multiply,
}

public enum Format
{
    Unknown,
    R8G8B8A8UNorm,
    R16G16B16A16Float,
    D32Float,
    D24UNormS8UInt,
}

public enum TextureDimension
{
    Texture1D,
    Texture2D,
    Texture3D,
    Cube,
    Texture2DArray,
    CubeArray,
}

public sealed class RenderGraphContext
{
    public CommandBuffer CommandBuffer => throw new NotSupportedException();
    public Texture GetTexture(TextureHandle handle) => throw new NotSupportedException();
    public TextureView GetTextureView(TextureHandle handle, in TextureViewDesc view) => throw new NotSupportedException();
    public BufferHandle GetBuffer(BufferHandle handle) => throw new NotSupportedException();
    public BufferView GetBufferView(BufferHandle handle, in BufferViewDesc view) => throw new NotSupportedException();
    public RayTracingAccelerationStructure GetRayTracingAccelerationStructure(
        RayTracingAccelerationStructureHandle handle) => throw new NotSupportedException();
}

public abstract class Texture { }
public abstract class TextureView { }
public abstract class BufferHandle { }
public abstract class BufferView { }
public abstract class CommandBuffer { }
public abstract class RayTracingAccelerationStructure { }

```

# 27. 参考资料

- **[B1] Bevy `pipelined_rendering.rs`** — RenderApp 所有权移交、容量 1 双向通道、主线程 extract、渲染线程 update、关闭时回收。
  https://github.com/bevyengine/bevy/blob/main/crates/bevy_render/src/pipelined_rendering.rs
- **[B2] Bevy `bevy_render/src/lib.rs`** — RenderApp、RenderSystems 和渲染 schedule。
  https://github.com/bevyengine/bevy/blob/main/crates/bevy_render/src/lib.rs
- **[G1] Themaister/Granite** — Render Graph 能力概览：history、alias、async、mipmap、resolve、subpass merge。
  https://github.com/Themaister/Granite
- **[G2] Granite `renderer/render_graph.hpp`** — Pass、资源、queue、physical resource、barrier 与 bake。
  https://github.com/Themaister/Granite/blob/master/renderer/render_graph.hpp
- **[R1] AMD Render Pipeline Shaders SDK** — 编译器式运行时、barrier、memory scheduling、visualizer。
  https://github.com/GPUOpen-LibrariesAndSDKs/RenderPipelineShaders
- **[R2] AMD RPS Tutorial Part 1** — frame index、gpu completed frame、temporal resources。
  https://gpuopen.com/learn/rps-tutorial/rps-tutorial-part1/
- **[R3] AMD RPS Tutorial Part 2/3** — resource views、derived mip views、multithreaded recording。
  https://gpuopen.com/learn/rps-tutorial/
- **[F1] FrameGraph: Extensible Rendering Architecture in Frostbite** — GDC 2017。
  https://www.gdcvault.com/play/1024045/FrameGraph-Extensible-Rendering-Architecture-in
- **[F2] Frostbite FrameGraph slide deck** — setup/compile/execute、transient、async compute。
  https://www.slideshare.net/slideshow/framegraph-extensible-rendering-architecture-in-frostbite/72795495
- **[U1] UE Render Dependency Graph** — 即时构图、资源、Pass、async、transient、validation、Insights。
  https://dev.epicgames.com/documentation/en-us/unreal-engine/render-dependency-graph-in-unreal-engine
- **[U2] `FRDGTextureSubresourceRange`** — mip、array slice、plane range。
  https://dev.epicgames.com/documentation/en-us/unreal-engine/API/Runtime/RenderCore/FRDGTextureSubresourceRange
- **[U3] `FRDGSubresourceState`** — per-subresource access、first/last pass、pipeline。
  https://dev.epicgames.com/documentation/en-us/unreal-engine/API/Runtime/RenderCore/FRDGSubresourceState
- **[U4] `FRDGTexture`** — per-subresource state、producer、view cache、transient allocation。
  https://dev.epicgames.com/documentation/en-us/unreal-engine/API/Runtime/RenderCore/FRDGTexture
- **[A1] Unity `RenderGraph.cs`** — C# RenderGraph、Create/Import、recording、EndFrame 等名称证据。
  https://github.com/Unity-Technologies/Graphics/blob/master/Packages/com.unity.render-pipelines.core/Runtime/RenderGraph/RenderGraph.cs
- **[A2] Unity `IRenderGraphBuilder.cs`** — attachment、async、culling、global state、VRS、foveated 等名称证据。
  https://github.com/Unity-Technologies/Graphics/blob/master/Packages/com.unity.render-pipelines.core/Runtime/RenderGraph/IRenderGraphBuilder.cs
- **[A3] Unity RenderGraph deprecated API** — ReadTexture/WriteTexture/ReadBuffer/WriteBuffer 等名称证据。
  https://github.com/Unity-Technologies/Graphics/blob/master/Packages/com.unity.render-pipelines.core/Runtime/RenderGraph/Deprecated.cs
- **[N1] What’s new in .NET 10** — .NET 10 / C# 14。
  https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-10/overview
- **[N2] .NET releases and support** — .NET 10 支持信息。
  https://learn.microsoft.com/en-us/dotnet/core/releases-and-support
- **[N3] `BoundedChannelOptions`** — 有界 Channel 与 Wait 背压。
  https://learn.microsoft.com/en-us/dotnet/api/system.threading.channels.boundedchanneloptions?view=net-10.0

---

**文档结束。** 一期验收以第 1.3、20、21、22、25 章和机器可读任务矩阵为准；任何“后续再补”的基础语义不属于本方案。
