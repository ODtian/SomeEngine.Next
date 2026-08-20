# 已撤回：Default Runtime Debug CPU E2E 假设审计记录

> Render Graph 的唯一现行设计契约是
> [Render Graph Wiki](../wiki/architecture/Render-Graph.md)。本撤回记录中的旧名称和
> 架构假设没有权威性。

> 撤回日期：2026-07-28  
> 当前裁决：**RETRACTED / NO-GO；不得作为候选方案或正式产品实现依据**  
> 撤回原因：用户要求每个优化点在被提出为候选以前就必须有具体、实际证据；原 v5 的 V5-02～V5-08 不满足该资格。  
> 本文件只保留错误审计记录，不再定义任何冻结候选集合。

## 0. 撤回声明

原 `PERF-RT-E2E-v5` 整体撤回。

必须采用的证据顺序是：

```text
真实 Default Runtime 实验取得证据
→ 正确性与架构证据成立
→ 与其他已证项联合运行达到完整目标
→ 才能把该项提出为优化候选
→ 所有合格候选一次冻结
→ 才允许正式产品实现
```

不得采用原文件隐含的以下顺序：

```text
先提出/冻结未经证明的候选
→ 再通过影子实现寻找证据
```

因此：

- V5-02～V5-08 全部撤回，不能称为候选优化、候选闭包或性能方案；
- V5-01 只有局部因果证据，但其完整真实整帧结果仍为 9.7946 ms 和 8.3748 ms，不能构成达到目标的方案；
- 当前不存在任何有证据实现完整端到端 p95 `<1.000 ms` 的候选集合；
- 下文中出现的“冻结”“候选”与 `V5-*` 名称只用于记录被撤回内容，不具有设计授权、实验授权或实现授权；
- 在完整真实证据出现以前，不得从下文选取任何未证机制进行产品实现。

## 1. 结论先行

本方案冻结八个且仅八个规划期影子候选闭包。冻结后不得在影子验证中追加、替换或暗改第九个优化点；若八项的完整真实影子候选不能通过本文件定义的联合证书，`PERF-RT-E2E-v5` 整体作废，不能进入正式产品实现，也不能在正式实现期继续“边测边搓”。

截至冻结时，**没有具体、实际证据证明这八项的完整真实组合可以使目标场景达到 p95 < 1.000 ms**。当前完整真实基线的 p95 为 **11.9928 ms**；唯一有两轮局部因果 A/B 证据的优化仍只把真实整帧候选做到 9.7946 ms 和 8.3748 ms。现有合成组合 witness 又分别得到 0.9732 ms 与 1.0281 ms，既不稳定，也不是完整 Default Runtime。

因此，严格遵守“规划期先确认可达、正式实现期禁止试错”的唯一诚实结论是：

- 当前可以冻结**候选验证方案**；
- 当前不能签发“已确认 < 1 ms”的**正式实现方案**；
- 产品代码的正式性能实现必须保持 `NO-GO`；
- 只有规划期完整真实影子候选取得本文件要求的联合证据后，才可把八项一次性升级为正式实现冻结清单。

任何直接宣称当前方案已经确认可达 `< 1 ms` 的说法，都与现有原始数据冲突。

## 2. 唯一目标与测量语义

### 2.1 目标

在以下固定环境和固定 Default Runtime 场景中，五个相互独立的新进程必须**各自**满足：

```text
p95(admitted input → real Present(1) return) < 1.000 ms
```

每个进程：

- warmup：8192 帧；
- samples：16384 帧；
- 不合并五个进程的样本；
- p50、p99、max、分配量和 GC 只报告，不作为通过条件；
- 不对任何组成部分设 `< 1 ms`、占比或局部预算。

这里的“CPU 总时间”按已确认口径，严格指**已准入帧在协调路径上的端到端墙钟关键路径**，不是 `Process.TotalProcessorTime`，也不是所有参与线程 CPU time 的求和。

### 2.2 计时边界

唯一计时边界为：

```text
input / window / UI
→ dynamic scene update
→ ECS extraction
→ render prepare
→ AcquireNextImage / target publish
→ Cluster + UI Graph author / close / compile
→ physical resource and view acquisition
→ native command recording
→ queue submission
→ diagnostics
→ real FIFO Present(1) return
```

以下工作保持在既有 admission 边界外：

- DXGI frame-latency admission；
- 等待 GPU retirement；
- completed-fence refresh；
- transient/command storage reclaim/reset；
- `AdmitFrameResources()`。

不得通过移动新的 CPU 工作到计时区间外、增加资源 generation、增加 future-frame 数据、延迟本帧必需工作或改变 admission 语义来达标。

### 2.3 固定环境

- CPU：Intel Xeon E5-2698B v3；
- GPU：NVIDIA GeForce RTX 3080；
- 电源计划：高性能；
- Runtime：.NET 10.0.9；
- Build：`Debug` 且 `Optimize=false`；
- `DOTNET_TieredCompilation=0`；
- `COMPlus_TieredCompilation=0`；
- `DOTNET_ReadyToRun=0`；
- `COMPlus_ReadyToRun=0`；
- 不附加 RenderDoc、Tracy、D3D12 validation、ETW profiler 或其他采样污染源。

最终证书必须反射检查所有被测自有 DLL 的 `DebuggableAttribute.DisableOptimizations`，并记录 DLL、PDB、shader、asset manifest、runtime asset、scene asset 和候选源码清单的 SHA-256。只检查 `SomeEngine.Runtime.dll` 不足以签发证书。

### 2.4 固定场景

- 1280 × 720；
- 1024 个动态实例；
- 完整默认 Cluster renderer；
- 默认 UI；
- 真实 D3D12；
- async compute 开启且保留真实 compute queue；
- FIFO `Present(1)`；
- 三个 swapchain buffers；
- maximum frame latency = 2；
- 不降低分辨率、实例数、pass、shader、材质、UI、可见性、光照或输出质量；
- 不允许 benchmark-only fast path。

## 3. 必须保持的架构

本方案从 `codex://threads/019f8805-9f22-7351-a001-c71d6df39eec` 最后一轮继承以下不可协商约束：

1. 四层边界不变：
   - L0：精确、不可变的事实和值；
   - L1：由 `Device` 唯一拥有的对象与 owner；
   - L2：`Recorder → finished command list → Queue` 是唯一提交路径；
   - L3：单次 invocation 的 `RenderGraph`。
2. `RenderGraph` 必须继续**单次使用**；每个 invocation 必须重写全部 canonical facts。
3. 禁止跨 invocation 的 compiled topology、reusable Graph、Graph template、command replay/cache、frozen rows 或任何等价变体。
4. canonical rows 是唯一 Graph 真相。compiler 只允许 invocation-local 的 CSR、bitset、ordinal、prefix/count/fill scratch；不得形成第二套 Graph model 或 shadow rows。
5. `QueuePosition` / `DevicePosition` 是唯一同步事实；不得引入并行的 fence token、submission token、future、packet、plan 或 frame wrapper。
6. transient physical pool 继续由 `Device` 拥有；Graph 只持有本 invocation 的 claims。
7. diagnostics 只能是执行后脱离 owner 的不可变投影，不能反向成为执行真相。
8. backend 不是第五个领域层，不能保存跨帧 Graph 事实。
9. owner 必须唯一；borrow 不拥有、不复制、不长期存储。
10. 所有同价选择必须使用 deterministic ordinal tie-break。
11. 不增加 compatibility wrapper，也不引入名称虽不同但职责等价的 `*Plan`、`*Packet`、`*Token`、`*Future`、`*Frame`、`*Submission` 中间模型。

正式候选若需要违反任一条，必须判定 `PERF-RT-E2E-v5` 失败；不能用性能结果覆盖架构失败。

## 4. 当前基线与反证

2026-07-28 在固定主机上重新执行：

```text
dotnet build src\SomeEngine.Runtime\SomeEngine.Runtime.csproj
  -c Debug
  --no-restore
  -p:Optimize=false
```

构建结果：0 warnings，0 errors。随后执行完整 8192 warmup + 16384 samples 的真实 D3D12 Default Runtime 基线。

原始 CSV：

- 路径：`tmp/runtime_perf_plan_baseline.csv`
- SHA-256：`354668E8ADFA52145C697A9533E8240C4C82E7C1EA81B0A1D5728494E1111F6B`

端到端结果：

| 统计量 | 结果 |
|---|---:|
| p50 | 7.8561 ms |
| p95 | 11.9928 ms |
| p99 | 13.6768 ms |
| max | 27.2668 ms |
| `>= 1 ms` 的样本 | 16384 / 16384 |
| allocation p50 | 137,192 B/frame |
| allocation p95 | 139,592 B/frame |
| allocation p99 | 145,992 B/frame |
| Gen0 | 110 / 16384 samples |

该基线使用 `ExecuteForBenchmark` 和细分时间戳，因而是带详细 instrumentation 的保守基线，不是最终证书路径。它仍然是有效反证：当前完整真实实现距离目标不是测量噪声级差距。

当前 Default Graph 的精确形状：

| 事实 | 数量 |
|---|---:|
| passes | 50（43 graphics + 7 compute） |
| resources | 85（65 buffers + 20 textures） |
| views | 307（228 buffer + 79 texture） |
| accesses | 368 |
| shader arguments | 296 |
| dependencies | 163 |
| logical barriers | 343 |
| command units | 53 |
| command tasks | 5 |
| submission batches | 4 |

现有四批次为：

```text
Graphics producer A
→ Graphics producer B
↘ Compute
→ final Graphics / UI / Present
```

直接压成一个 submit 会破坏现有跨 queue 依赖，明确禁止。

细分 p95 只用于定位，不得相加，也不得转化为局部性能门槛。`graph_frame` 包含 author、compiler、acquisition、record、submit 和 cleanup；这些列是嵌套关系，不是可加总的互斥组成。

## 5. 冻结的八个规划期影子候选闭包

以下八项是 `PERF-RT-E2E-v5` 的完整集合。每项的“预期”均为具体机制结果，不承诺一个人为拆分的毫秒预算；唯一性能结果仍是完整联合端到端 p95。

### V5-01：Runtime 稳态数据面

冻结内容：

- 只在两个已定位的 value-write 边界抑制 Runtime-only serialization journal；
- dense transform update 使用 admitted 的连续值写入；
- extraction 使用固定 source-slot → mirror-slot 映射和 source-local 顺序；
- `Prepare` 仅走已证明无 streaming 状态变化时的稳定同步路径；
- 不改变 ECS owner、serialization 对外语义、streaming 状态机或 scene 输出。

已有实际证据：

- 默认帧精确产生 3072 个无外部消费者的 serialization journal events：
  - 1024 `WorldTransform`；
  - 1024 `RenderTransform`；
  - 1024 `RenderPreviousTransform`。
- 12 个 journal pages × 8216 B = **98,592 B/frame**。
- 两轮候选的 allocation p95：
  - 139,592 → 41,000 B，差值精确为 98,592 B；
  - 140,072 → 41,000 B，差值为 99,072 B。
- 两轮 scene + extract p95：
  - 1.4894 → 0.5192 ms；
  - 1.5896 → 0.4847 ms。
- Graph diff 为零；1024 个 mirror fields 等价；normalized Graph JSON 除 timing 外等价。

证据结论：**局部机制已证，整帧目标未证**。真实候选整帧 p95 仍为 9.7946 ms 和 8.3748 ms。

### V5-02：单次使用 Graph 的精确 authoring

冻结内容：

- pipeline owner 提供 canonical shader facts；
- generator 为六类已知 primitive 提供精确 count；
- 单次使用 Graph 仍在每帧重写全部 facts；
- 只回收 semantic-free pages/capacity 到既有 owner/pool；
- 不保留上一 invocation 的 row、handle、dependency、compiled topology 或命令。

现有事实依据：

- 默认 Graph 每帧固定重写 50 passes、85 resources、307 views、368 accesses、296 shader arguments。
- 当前 `AppendCanonicalAccess` 在同-pass access 间产生精确 **2064** 个 overlap 候选比较；单 pass 最大 32 accesses，对应 496 个候选。
- author 的真实细分 p95 已定位在毫秒级区间。

尚缺的规划期实际证据：

- 在完整真实 Default Runtime 上的单项 source-equivalent A/B；
- canonical row-by-row 等价；
- owner 生命周期、empty-fact 重写与 invocation 单次使用审计；
- 与其余七项组合后的完整端到端结果。

证据结论：**仅有真实热点和固定工作量证据，尚未证明候选收益**。

### V5-03：canonical compiler 的 invocation-local 降低

冻结内容：

- 使用 invocation-local count/prefix/fill CSR、bitset 和 ordinal scratch；
- liveness、hazard、barrier、placement、unit/task/batch lowering 只读取 canonical rows；
- 最终 canonical execution rows 一次写入；
- scratch 执行后立即归还，不成为第二 Graph model；
- 不缓存跨 invocation 的编译结果。

现有事实依据：

- 当前编译确有固定 canonical rows 的多轮遍历、85-resource 线性索引和至多 3570 个重复引用比较；
- 当前执行 storage 每帧至少 12 次 array rent、约 17 次 `clearArray:true` return；
- 已有合成 canonical CSR kernel witness，但不是完整真实 Runtime。

尚缺的规划期实际证据：

- 完整 50-pass Default Graph 上的 canonical input/output differential；
- scratch 生命周期和“无第二真相”审计；
- 单项真实 A/B；
- 联合端到端证书。

证据结论：**机制合理性与合成证据存在，真实完整收益未证**。

### V5-04：upload 与 physical acquisition 的批量 claim

冻结内容：

- 当前 19 个、合计 4664 B 的每帧 upload buffer 放入既有 frame-generation-bound persistent mapped upload region；
- 按 canonical ordinal 批量 claim resources/views；
- coordinator 在 owner 不变的前提下预解析 native owner/descriptors；
- 不新增 generation，不跨 generation 借用，不把 physical ownership 放入 Graph。

现有事实依据：

- 已确认 19 个 upload buffers、4664 B/frame 的精确规模；
- 已确认 85 resources、307 views 的 acquisition 工作量；
- 当前 acquisition 和 descriptor realization 在真实路径上可测。

尚缺的规划期实际证据：

- frame generation 生命周期证明；
- 对齐、容量、wrap、GPU retirement 和写后读规则证明；
- resource/view identity 与 native descriptor differential；
- 单项真实 A/B；
- 联合端到端证书。

证据结论：**只有规模与热点证据，候选尚未实证**。

### V5-05：barrier 的等价收缩

冻结内容：

- split transition 在 consumer 前立即完成；
- 只合并中间没有 GPU command 的相邻 barrier groups；
- texture cells 只有在 resource、state、flags 和连续 subresource 全部相同的情况下合并；
- 不删除真实 hazard，不更改 producer/consumer 次序或最终 resource state。

静态候选结果：

- logical barriers：343 → 266；
- native barrier batches：85 → 52；
- 另有 40 个 cell 可精确收缩为 rectangle。

尚缺的规划期实际证据：

- 343 条原 barrier 到候选 barrier 的逐项 coverage 映射；
- D3D12 debug/validation 的独立非计时运行；
- final resource state、queue ownership 和 shader output differential；
- 单项真实 A/B；
- 联合端到端证书。

证据结论：**已有确定的静态收缩结果，但尚无完整真实执行证明**。

### V5-06：两个预激活 record lanes

冻结内容：

- 只使用既有 frame-latency worker；
- worker 在 admission 外激活；
- measured interval 内只传递 primitive sequence/span；
- coordinator 与 worker 录制互不重叠的 canonical spans；
- 同帧 join 后才能提交；
- 禁止通用 Job、`Monitor.Pulse`、`Task`、`TaskCompletionSource`、busy spin、owner table lookup和临时 barrier/descriptor arrays。

现有事实依据：

- 当前已经有两个 record lanes，但每帧 handoff 仍经过 lock/`Monitor`；
- 当前每帧有 5 次 recorder acquire、5 次 Finish/Close、50 次 pass callback、343 个 logical barriers；
- 当前录制是完整真实路径的主要热点之一。

尚缺的规划期实际证据：

- 预激活 handoff 的真实 Default Runtime A/B；
- 调度、唤醒、join、错误传播和 shutdown 证明；
- 两个 lane 的 deterministic canonical span 与 command-list differential；
- 联合端到端证书。

证据结论：**真实热点已证，新 handoff 机制未证**。现有“双 lane”不能重复冒充本项收益。

### V5-07：三段 Queue schedule

冻结内容：

- 接受减少部分 GPU overlap，以降低 CPU submission transaction；
- 保留真正的 compute queue 和 async compute；
- 将六个 final HiZ compute passes 移入同一 compute island；
- 固定调度为：
  1. Graphics producer：两条 command lists，一次 submit；
  2. Compute island：一次 submit；
  3. final Graphics / UI：一次 submit，随后真实 Present；
- submission batches 只允许 4 → 3；
- 只使用既有 `Queue`、`DevicePosition` 和合法 wait/signal；
- 禁止 4 → 1、第二 graphics/present queue、新同步类型和额外 generation。

现有事实依据：

- 当前精确为四次 backend Submit、四次 `ExecuteCommandLists`、四次 fence Signal；
- 当前 Graph 的 task/batch 依赖已经明确；
- 六个 final HiZ passes 的移动范围和允许牺牲的 GPU overlap 已经获得口径确认。

尚缺的规划期实际证据：

- 六个 pass 的 input/output/resource-state/order 等价证明；
- 三段 schedule 的 queue-position 映射；
- GPU timestamp 与无额外等待证明；
- 单项真实 A/B；
- 完整输出与联合端到端证书。

证据结论：**调度变换边界已冻结，性能和输出尚未实证**。

### V5-08：critical-path 并行闭包

冻结内容：

- frontend/UI 与 dense scene-value lane 并行；
- `Prepare` 必须在关闭同一个单次使用 Graph 前发布完本帧所需事实；
- compile 完成后由两个 record lanes 并行录制；
- 三段 submit 前，本帧所有 command lists 和数据必须完成；
- 不使用 future-frame 数据，不缓存跨帧 Graph/command，不增加资源 generation。

现有事实依据：

- 已确认 frontend、scene/extract、prepare、Graph author/compile/record/submit 的真实依赖边界；
- 已确认 UI 已经在同一个 Graph 内，不能把“合并 UI Graph”重复计作优化；
- 已确认两个 record lanes 已存在，但 handoff 仍有固定控制开销。

尚缺的规划期实际证据：

- data-race、publication、owner、failure/cancellation、determinism 证明；
- 完整真实 callback 与输出 differential；
- 单项真实 A/B；
- 联合端到端证书。

证据结论：**依赖图可描述，完整机制未实证**。

## 6. 明确排除的伪优化

下列项目不属于 v5，影子验证和正式实现均不得采用：

- 重用同一个 `RenderGraph`；
- 跨 invocation 保存 compiled graph、topology、rows、handles 或 command lists；
- Graph template、frozen rows、command replay/cache；
- 第二套 Graph model、shadow rows 或 backend-owned Graph facts；
- 将 transient physical resource ownership 转给 Graph；
- 新增 resource generation 或把本帧工作推迟到未来帧；
- benchmark-only fast path；
- Null backend、empty callback 或 synthetic pass 代替真实 Default Runtime；
- 降低分辨率、实例数、shader、pass、UI、async compute 或输出质量；
- 关闭真实 FIFO Present；
- 4 → 1 submit；
- 第二 graphics queue、第二 present queue或新的同步 token/future/packet；
- 通过 busy spin、无限制线程数或不受控 thread-pool 工作隐藏延迟；
- 把 detailed instrumentation 的 component p95 相加；
- 选择最好的一次 synthetic run 作为证书；
- 复用 `.tmp/.../v3-shadow`：该候选跨帧复用了同一个 Graph，违反单次 invocation 架构；
- 使用 `SomeEngine.Graphics.Benchmarks` 的 Null `ImmediateRenderGraphScenario` 证明本目标。

## 7. 规划期影子联合证书

### 7.1 冻结清单

开始影子候选前必须生成只读 manifest，包含：

- 八个闭包的唯一 ID 和本文件 SHA-256；
- reference 源码、项目文件、assets、shaders、manifest 的 SHA-256；
- 允许修改的文件和每个文件对应的闭包；
- 禁止修改的架构文件/事实；
- 编译器、SDK、Runtime、OS、CPU、GPU、driver、电源计划；
- 五个独立运行的 affinity、priority 和环境变量；
- 唯一允许的 Graph 形状差异：
  - barrier 等价收缩；
  - 六个 final HiZ pass 的 compute-island placement；
  - submission batches 4 → 3。

冻结后：

- 不得添加优化项；
- 不得改变 workload；
- 不得改变目标统计量；
- 不得扩大允许差异；
- 不得把失败解释为“还差一点，再调一个参数”。

### 7.2 影子候选规则

规划期影子候选必须：

- 使用完整 Default Runtime、真实 D3D12、完整 Cluster callbacks、UI、async compute 和真实 Present；
- 一次实现八个冻结闭包；
- 与正式产品树隔离，不能把未证候选提前合并为产品实现；
- 保持单次使用 Graph；
- 不读取 benchmark mode 来选择优化路径；
- 产生可逐文件比对的冻结 patch；
- 编译和运行的 binary/source/asset hashes 一一绑定。

“影子候选”是规划期验证工件，不是规避“实现前冻结”的借口：八项已经先冻结；影子失败时只能整体拒绝 v5。

### 7.3 正确性证书

正确性验证与性能采样分开执行，不进入 measured interval。必须同时证明：

1. 1024 个动态实例的 source、mirror、previous/current transform 等价；
2. scene、camera、UI inputs、shader/material/pipeline identity 等价；
3. canonical Graph facts 等价；只允许 7.1 所列的两类结构差异；
4. barrier coverage、queue position、resource final state 和 command order 等价；
5. candidates、visible clusters、shaded pixels、deform cache 健康且等价；
6. reference/candidate frame output 在固定输入序列上逐像素或按已冻结的无损 readback hash 等价；
7. 多次 invocation 间不存在保留的 Graph facts、handles、compiled rows 或 command replay；
8. D3D12 validation 的非计时运行无错误；
9. 所有自有 DLL 均为 `DisableOptimizations`；
10. correctness binary 与 performance binary 的源码、assets、shaders 和 compilation inputs 相同；只允许是否启用区间外 readback/validation 的运行参数不同。

若第 3 项因 barrier/schedule 合法变化不能逐行相同，必须提供从 reference canonical facts 到 candidate canonical facts 的全覆盖映射；“画面看起来一样”不能代替资源状态和依赖证明。

### 7.4 性能证书

最终规划期性能证书必须使用普通 `graph.Execute()` 路径。measured interval 只允许：

- 一个外层起始 timestamp；
- 一个外层结束 timestamp；
- 采样值写入预分配的原始存储。

不得在 measured interval 内：

- 调用 `ExecuteForBenchmark`；
- 读取 component timestamps；
- 生成 JSON、CSV 或 diagnostics；
- 做 output readback；
- 计算 p50/p95/p99；
- 检查任何 `< 1 ms` 条件；
- 触发额外 GC、allocation、logging 或 file I/O。

每个进程退出后只导出原始 tick 序列和不可变环境/哈希清单。repo 内分析器只负责输出分位数，不包含 `1.000 ms` 常量或 pass/fail gate。是否满足唯一总目标由本文件对应的外部证据记录判定。

五个独立进程必须全部完成 8192 warmup + 16384 samples；不得重跑并挑选最好的五次，不得合并样本，不得删除 outlier。

### 7.5 单项因果证书

完整八项候选只有在先通过联合证书后，才进行固定 ablation：

- 每次只关闭一个已冻结闭包；
- reference、workload、其他七项、binary identity 规则和运行契约不变；
- 记录端到端原始分布、分配、固定工作量变化和正确性结果；
- ablation 不设置局部 `< 1 ms` 门槛；
- 不要求七项版本仍通过总目标；
- 每项必须证明它实际删除了本文件承诺的工作，且没有把工作移到边界外。

若某项在完整真实场景中没有可重复的因果效果，必须从下一版规划重新开始；不能在 v5 内静默删除、替换或添加项目。

### 7.6 GO 条件

只有以下条件全部成立，才签发正式实现 `GO`：

- 八项的 correctness 证书全部通过；
- 八项的 architecture 审计全部通过；
- 五个独立进程各自 p95 < 1.000 ms；
- 每项均有真实 Default Runtime 的因果证据；
- shadow patch、source、assets、binary 和报告 hashes 完整；
- 没有采用第 6 节的排除项；
- 没有任何 component performance gate；
- 没有对冻结集合做事后调参或扩项。

任一失败，结论只能是 `NO-GO`。

## 8. GO 后的正式实现约束

正式实现不是再次探索性能的机会。它只能把已通过的影子 patch 按冻结 manifest 等价落入产品树：

- 八项作为一个端到端结果交付；
- 不添加、删除、替换或重新设计优化；
- 不改变 public API 和四层架构；
- 不将 `1 ms` 或拆分预算写入 unit test、integration test、benchmark test、harness gate 或 CI；
- repo 内测试只验证行为、架构、owner/lifetime、canonical facts、输出和 deterministic order；
- 完成后重复同一外部联合证书，只验证正式落地与已证影子一致；
- 若正式结果与影子证书不一致，停止并判定实现不等价；禁止通过新增优化继续追目标。

换言之，正式实现期只能证明“照着已证方案实现正确了”，不能承担“发现还不够快再继续优化”的职责。

## 9. 当前证据裁决

| 要求 | 当前状态 | 裁决 |
|---|---|---|
| 目标和边界冻结 | 已完成 | PASS |
| 架构约束冻结 | 已完成 | PASS |
| workload / machine / build 冻结 | 已完成 | PASS |
| 八个影子候选闭包冻结 | 已完成 | PASS |
| 完整真实基线 | 已完成，p95 11.9928 ms | PASS（反证） |
| V5-01 局部因果证据 | 两轮完成 | PASS（仅局部） |
| V5-02…V5-08 单项真实因果证据 | 未完成 | FAIL |
| 八项完整真实 correctness | 未完成 | FAIL |
| 八项完整真实 architecture audit | 未完成 | FAIL |
| 五个独立完整真实进程各自 p95 < 1 ms | 未完成 | FAIL |
| 规划期确认可以达到目标 | 未成立 | **FAIL** |

最终裁决：

```text
PERF-RT-E2E-v5 = NO-GO
Formal product implementation = FORBIDDEN
Next allowed action = none under this withdrawn v5
```

不得为本文件中的撤回项建立联合影子实现。若以后存在新的方案，它必须先以已经取得的完整真实证据出现，而不是从本文件的未证假设继续试错。本文件只能用于说明 v5 为什么被撤回。
