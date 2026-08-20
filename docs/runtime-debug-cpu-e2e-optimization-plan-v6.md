# Default Runtime Debug CPU E2E 性能闭环方案

> **性能证据方案，不是 Render Graph 设计。** 当前 Render Graph 架构、名称和验收口径只
> 取自 [Render Graph Wiki](../wiki/architecture/Render-Graph.md)；本文中的旧 API 名称和
> 对旧审计的引用不得建立或修改架构条款。

> 日期：2026-07-28  
> 方案代号：`PERF-RT-E2E-v6`  
> 当前状态：**EVIDENCE BUILDING / NO-GO**  
> 唯一目标：在冻结的 Default Runtime 场景、Debug 且 `Optimize=false` 条件下，五个独立进程各自满足  
> `p95(admitted input → real FIFO Present(1) return) < 1.000 ms`

## 0. 结论

当前实现尚未达到目标，不能签发“已确认可达”或“正式实现 GO”。

当前源码连续两次普通 `RenderGraph.Execute()`、outer-only、真实 D3D12 短样本结果为：

| 统计量 | run A | run B |
|---|---:|---:|
| warmup | 512 帧 | 512 帧 |
| samples | 2,048 帧 | 2,048 帧 |
| p50 | 6.4968 ms | 6.7696 ms |
| p95 | 7.5991 ms | 11.8865 ms |
| p99 | 8.5680 ms | 12.9132 ms |
| max | 12.3848 ms | 19.1465 ms |
| `>= 1 ms` | 2,048 / 2,048 | 2,048 / 2,048 |

原始数据：

- `tmp/runtime-outer-only-final2.csv`
- `tmp/runtime-outer-only-final3.csv`
- `timing_mode=outer-only`
- `Debug; Optimize=false`
- tiered compilation 与 ReadyToRun 均关闭
- NVIDIA GeForce RTX 3080
- 1,024 个动态实例
- async compute 开启
- FIFO `Present(1)`

这两个运行只用于当前方向裁决，不是最终证书：样本数量仍小于最终要求的
`8192 warmup + 16384 samples × 5 processes`。
相同 binary/参数的两个 p95 相差 4.2874 ms，说明短运行的系统噪声本身仍未受控；方案不得选择
run A 而丢弃 run B。

当前只有两个产品改动具备真实 Default Runtime 局部因果证据：

1. Runtime-only value write 边界抑制无消费者的 serialization journal。
2. pass 内 canonical access 冲突检查使用 invocation-local、同 resource 前驱索引。

其余机制只能作为待验证实验，不能先称为候选优化，更不能预先宣称联合后会达到
`<1 ms`。本文件的职责是定义：

- 哪些改动已经取得资格；
- 哪些假设已经被真实 A/B 否决；
- 下一项实验必须删除哪一段具体工作；
- 每项如何保持既定四层架构；
- 何时才能把实验升级为正式实现清单；
- 最终证书如何证明唯一总目标。

## 1. 冻结目标与测量语义

### 1.1 唯一性能门槛

五个相互独立的新进程必须分别满足：

```text
p95(admitted input → real FIFO Present(1) return) < 1.000 ms
```

每个进程：

- warmup：8,192 帧；
- samples：16,384 帧；
- 不合并五个进程的样本；
- 不删除 outlier；
- 不从多次运行中挑选最好的五次；
- p50、p99、max、allocation、GC 只报告，不替代唯一 p95 门槛；
- 不给分段时间设置可以替代总目标的局部门槛。

这里的 CPU E2E 是协调路径墙钟关键路径，不是 `Process.TotalProcessorTime`，也不是参与线程
CPU time 的求和。

### 1.2 measured interval

唯一计时边界：

```text
input / window / UI
→ dynamic scene update
→ ECS extraction
→ render prepare
→ AcquireNextImage / target publish
→ Cluster + UI Graph author / close / compile
→ physical resource and view acquisition
→ native command recording
→ Queue submission
→ diagnostics check
→ real FIFO Present(1) return
```

以下既有 admission 工作仍在区间外：

- DXGI frame-latency admission；
- GPU retirement wait；
- completed-fence refresh；
- transient/command storage reclaim/reset；
- `AdmitFrameResources()`。

禁止把新的本帧 CPU 工作移到区间外，禁止增加 future-frame 数据、资源 generation 或延迟本帧
必需工作来缩短表面时间。

### 1.3 最终计时实现

最终证书只能使用：

```text
--benchmark-outer-only
```

该模式要求：

- sampled frame 走普通 `graph.Execute()`；
- measured interval 内只有一个外层开始 timestamp 和一个外层结束 timestamp；
- sampled frame 的正常 frame-time 计算复用外层开始 timestamp，不再读取第三次顶层 timestamp；
- sampled frame 不调用 `ExecuteForBenchmark`；
- 不读取 component timestamps；
- component tick 列全部为零；
- 原始 tick 预分配存储，进程退出后才输出 CSV 与统计量；
- Graph snapshot 只在排除于 samples 的边界帧生成。

`--benchmark-breakdown` 或默认 breakdown 模式只用于定位，不得签发最终证书。

## 2. 不可协商的架构边界

本方案严格继承
`codex://threads/019f8805-9f22-7351-a001-c71d6df39eec`
最后一轮以及 `docs/rhi-render-graph-concept-audit.md` Section 6、9、10 的终态：

1. L0 只保存精确、不可变的事实和值。
2. L1 的生命周期实体由 Device 唯一拥有。
3. L2 只有 `ICommandRecorder → CommandList → Queue` 一条提交路径。
4. L3 的 `RenderGraph` 是一次 invocation 的 single-use owner。
5. 每个 invocation 必须重新写入全部 canonical Graph facts。
6. 禁止 reusable Graph、Graph template、compiled topology cache、frozen rows、command replay/cache，
   以及任何名称不同但职责等价的跨 invocation 保留结构。
7. canonical rows 是唯一 Graph 真相。
8. compiler 只可使用 invocation-local CSR、bitset、ordinal、prefix/count/fill、index 与工作集合。
9. compiler scratch 不能形成第二套 Graph model、shadow rows 或独立 lifecycle。
10. `QueuePosition` 与 `DevicePosition` 是唯一 GPU 同步事实。
11. 禁止 fence/submission token、future、packet、plan、result 或 frame wrapper 重新表达同步。
12. transient physical pool 继续由 Device 拥有；Graph 只拥有本 invocation 的 claims。
13. diagnostics 只能从 canonical facts 显式 materialize detached immutable Snapshot。
14. backend 只是 L1/L2 的实现，不是第五层，也不能保存跨帧 Graph facts。
15. owner 唯一；borrow 不拥有、不复制、不长期存储。
16. 所有同价选择都使用 deterministic ordinal tie-break。
17. 不新增 compatibility wrapper、adapter、双写状态或迁移期 API。

任何实验一旦需要违反上述任一条，立即判失败；性能结果不能覆盖架构失败。

## 3. 当前真实证据

### 3.1 初始基线

同一主机、Debug、`Optimize=false`、真实 D3D12、512 warmup + 2,048 samples：

| 项目 | p50 | p95 |
|---|---:|---:|
| CPU E2E | 7.6720 ms | 10.4002 ms |
| Graph frame | — | 8.01 ms |
| command encoding | — | 2.90 ms |
| scene + extract | — | 1.53 ms |
| Graph author | — | 1.12 ms |
| command submit | — | 1.07 ms |
| Present | — | 0.69 ms |
| allocation | — | 139,592 B/frame |

原始数据：`tmp/current-runtime-baseline.csv`。

### 3.2 journal 抑制后的 breakdown

同口径结果：

| 项目 | p50 | p95 |
|---|---:|---:|
| CPU E2E | 6.6793 ms | 8.0364 ms |
| Graph frame | 5.5349 ms | 6.6645 ms |
| Graph author | 0.70 ms | 1.03 ms |
| compiler liveness | 0.30 ms | 0.36 ms |
| compiler validation | 0.16 ms | 0.20 ms |
| compiler dependencies/barriers | 0.33 ms | 0.39 ms |
| compiler placement/grouping | 0.47 ms | 0.53 ms |
| compiler execution lowering | 0.32 ms | 0.38 ms |
| resource acquisition | 0.18 ms | 0.23 ms |
| view acquisition | 0.20 ms | 0.26 ms |
| command encoding | 1.81 ms | 2.52 ms |
| command submit | 0.70 ms | 0.80 ms |
| command cleanup | 0.18 ms | 0.22 ms |
| frontend | 0.09 ms | 0.13 ms |
| scene + extract | 0.32 ms | 0.44 ms |
| prepare | 0.29 ms | 0.37 ms |
| Present | 0.40 ms | 0.56 ms |
| allocation | 39,304 B/frame | 41,000 B/frame |

原始数据：`tmp/runtime-journal-suppressed.csv`。

同一批逐帧样本中，`cpu_ticks - graph_frame_ticks` 的 p95 为 **1.4522 ms**。因此只优化
RenderGraph/compiler/record/submit，即使把整个 Graph frame 假设为零，现有 Graph 外路径仍不能满足
总目标。Runtime 数据面、prepare 和 Present 尾部必须一起进入闭环。

### 3.3 Default Graph 的 canonical 规模

边界 Graph snapshots 一致，默认调用精确包含：

| canonical fact | 数量 |
|---|---:|
| passes | 50（43 Graphics + 7 Compute） |
| resources | 85（65 Buffer + 20 Texture） |
| views | 307（228 Buffer + 79 Texture） |
| accesses | 368 |
| shader arguments | 296 |
| dependencies | 163 |
| logical barriers | 343 |
| command units | 53 |
| command tasks | 5 |
| submission batches | 4 |

所有后续 differential 都必须以这些 canonical inputs、输出与允许的等价映射为基准。

## 4. 已取得资格的产品改动

### Q-01 Runtime-only serialization journal 抑制

#### 改动

- `RuntimeScene.Update` 的 `WorldTransform` value-write query 在
  `World.SuppressSerializationJournal` 边界内执行。
- `RenderExtractionSystems.Extract` 在 render mirror world 的同类边界内执行。
- 只抑制 Runtime 每帧覆盖写、且没有 serialization consumer 的 journal。
- ECS 对外 serialization、owner、query 与 change-version 语义不变。

#### 因果证据

- 每帧恰好删除 3,072 个无外部消费者的 journal events：
  - 1,024 `WorldTransform`；
  - 1,024 `RenderTransform`；
  - 1,024 `RenderPreviousTransform`。
- 12 个 journal pages × 8,216 B = 98,592 B/frame。
- allocation p95：139,592 → 41,000 B/frame，差值恰好 98,592 B。
- scene + extract p95：1.53 → 0.44 ms。
- 短样本 CPU E2E p95：10.4002 → 8.0364 ms。
- Graph before/after normalized diff 为空。

#### 架构裁决

通过。该改动不创建 RHI/RG 节点，不移动 owner，不保留跨 invocation Graph facts，也不改变
L0–L3。

### Q-02 pass-local same-resource access index

#### 改动

- canonical `AccessRow` 仍按原 ordinal 直接写入 Graph-owned arena。
- 新增的 `_accessPredecessors` 只保存“同一 pass、同一 resource 的前一条 canonical access
  ordinal”。
- buffer/texture access head 使用 `pass + 1` stamp，默认零表示本 pass 尚未出现。
- 冲突检查只沿同 resource 链调用原有 `AccessNormalizer.Overlaps` 与
  `IsReadOnlyDepthLocalRead`。
- 索引与 Graph invocation 一同销毁；不跨 invocation，不参与 execution，不成为事实源。

#### 因果证据

相邻未改对照：

| 运行 | CPU p50 | CPU p95 | author p50 | author p95 |
|---|---:|---:|---:|---:|
| control | 6.5035 ms | 7.4564 ms | 0.71 ms | 0.83 ms |
| index run A | 6.3846 ms | 7.2621 ms | 0.65 ms | 0.76 ms |
| index run B | 6.4105 ms | 7.3776 ms | 0.66 ms | 0.80 ms |

原始数据：

- `tmp/runtime-access-control-repeat.csv`
- `tmp/runtime-access-index.csv`
- `tmp/runtime-access-index-repeat.csv`

收益很小，但方向和局部 author 指标在两次 candidate run 中一致；没有把这项收益重复计入其他
工作流。allocation p95 仍为 41,000 B/frame。

#### 架构裁决

通过。它是 Section 9 明确允许的 invocation-local `Index`，只加速 canonical row validation；
canonical rows 仍是唯一真相。

#### 仍需补齐

- full 8,192 + 16,384 单项重复运行；
- 全解决方案 build/test；当前 `SomeEngine.slnx` 被本改动范围外的 ECS `Archetype`
  `ReadOnlySpan` / `IReadOnlyList` 三处类型不匹配阻塞。

当前已增加并通过两个定向测试，分别覆盖：

- 同一 resource 的两个不相交 range 被另一 resource access 隔开时仍接受；
- 同一 resource 的两个重叠 range 被另一 resource access 隔开时仍拒绝。

定向测试随 `SomeEngine.RenderGraph.Tests` 全部 170 项通过；RenderGraph 与 Runtime 项目也分别在
Debug、`Optimize=false`、`BuildProjectReferences=false` 下零警告零错误。长样本与完整解决方案
验证完成前，Q-02 仍是“局部合格项”，不是最终证书项。

## 5. 已否决的假设

### R-01 “打开编译优化就能解释差距”

诊断性 `Debug + Optimize=true` 运行得到 p95 7.3095 ms，仍远高于 1 ms，而且不符合用户要求。
不能通过改变 build contract 达标。

原始数据：`tmp/runtime-opt-diagnostic.csv`。

### R-02 “当前双 record lane 本身是主要固定开销，改串行会更快”

相邻真实运行：

| 录制 | CPU p50 | CPU p95 | CPU p99 |
|---|---:|---:|---:|
| 当前双 lane | 6.5035 ms | 7.4564 ms | 8.6888 ms |
| 强制串行 | 6.4937 ms | 7.7273 ms | 10.3102 ms |

串行没有降低中位数，并恶化尾部。实验改动已撤回。后续不能把“去掉并行”写成优化；应优化
现有固定 lane 的 handoff/ready/submission 关键路径。

原始数据：

- `tmp/runtime-access-control-repeat.csv`
- `tmp/runtime-record-serial.csv`

### R-03 “每帧 41 KB 都来自 transient descriptor 创建 lambda”

把 D3D12 view descriptor 路径改为直接 allocate + native create 后：

- allocation p95 仍为 41,000 B/frame；
- view acquisition 没有可重复下降；
- CPU p95 7.4564 → 7.4838 ms。

默认稳态显然主要复用了 Device-owned physical views；trace 中的 closure/delegate 更可能来自启动、
补充创建或回收边界。实验改动已撤回，不能纳入方案。

原始数据：`tmp/runtime-direct-descriptors.csv`。

## 6. 待验证实验闭包

以下条目是**实验**，不是已经获准的优化候选。每项必须先在完整 Default Runtime 上取得本节规定的
局部因果、正确性与架构证据；没有证据就删除实验 patch，不能靠合理性描述晋级。

### E-01 剩余 allocation 的精确归因

#### 已知事实

- journal 抑制后 allocation p95 仍为 41,000 B/frame。
- sampled allocation trace 出现 `Entity[]`、`Byte[]`、`Int32[]`、`FrameResources`、
  `RenderPrepareScope`、`RenderGraph`、`CommandRecorder`、`NativeBufferMapping` 等类型。
- GC allocation tick 是采样事件，不能把采样次数直接解释成每帧精确数量。

#### 实验

1. 在定位运行中使用 `GC.GetAllocatedBytesForCurrentThread()` 和 worker-owned allocation counter，
   只记录各阶段前后差值；该探针不进入最终证书。
2. 对 scene/extract、prepare、Graph author/compile、acquisition、record/cleanup 分别取得 exact
   coordinator/worker allocation。
3. 对 exact 非零阶段再收集 allocation stack，绑定到具体构造点。
4. 每次只删除一种已确认的 per-frame allocation，重复真实 A/B。

#### 禁止

- 对 Graph、FrameResources、command list 或 descriptor owner 做跨 invocation 对象池并保留旧事实；
- 为减少 allocation 把 unique owner 变成 copyable handle；
- 用 thread-static 或 backend cache 保存 Graph rows；
- 把 allocation 工作移动到 admission 外。

#### 晋级证据

- exact type、size、count、owner 与调用栈；
- 删除前后 allocation differential；
- owner/lifetime/dispose failure-path 测试；
- CPU E2E 真实 A/B。

### E-02 command recording 与 ordinal submission 重叠

#### 已知事实

- 5 个 canonical command tasks 已经存在。
- 当前流程是：取得全部 recorders → 两 lane 完成全部 encoding → join → 顺序提交 4 个 batches。
- command encoding p50/p95 为 1.81/2.52 ms。
- submit p50/p95 为 0.70/0.80 ms。
- 强制串行录制已被否决。

#### 实验

仍使用既有 `CommandTask`、`CommandBatch`、`CommandList` 与 `Queue`：

1. 按 canonical task ordinal 把 5 个 task 分配给固定、预激活的 record workers。
2. worker 只借 invocation-owned、不重叠的 command-unit spans。
3. `Finish()` 直接把唯一 `CommandList` owner 写入 Graph-owned task slot。
4. coordinator 严格按 canonical batch ordinal 检查 batch 所需 task 是否全部完成。
5. 当前 batch ready 后立即经现有 `Queue.Submit` 消费 finished owners，同时其他独立 task 继续录制。
6. batch wait 仍只从 canonical dependencies、external waits 与已发布 `QueuePosition` 计算。
7. 所有 batch 提交后，仍由 `DevicePosition` 合并最终发布坐标。

CPU worker readiness 只能是 invocation-local primitive control state，不能升级为
submission token/future/packet/result；不得保存到 backend 或下一 invocation。

#### 必须证明

- 每个 finished `CommandList` 恰好被 Submit 或 Dispose 一次；
- failure 时已经提交的 `QueuePosition` 仍通过现有 `RenderGraphExecutionException` 发布；
- 未提交 owner 全部释放；
- batch ordinal、waits、signals、command-list sequence 与 reference 等价；
- 不 busy-spin，不使用 `Task`/`TaskCompletionSource`，不创建通用 job graph；
- 输出与资源最终状态等价；
- 真实 A/B 删除了“全部录制结束后才开始第一 submit”的空窗。

### E-03 canonical authoring 的预留与直接填充

#### 已知事实

- 每帧重写 50 passes、85 resources、307 views、368 accesses、296 shader arguments。
- Q-02 已删除 pass 内无关 resource 的冲突比较，但 author p50 仍约 0.65 ms。
- `IPassParameters<TSelf>` generator 已拥有静态声明 ABI。

#### 实验

1. generator 为每个参数类型输出六类 canonical primitive 的精确 count。
2. Graph owner 只预留本 invocation 的最终 canonical row ranges。
3. producer 直接写最终 rows，ordinal 由 prefix sum 决定。
4. empty facts 也在本 invocation 明确写入，不从上一帧继承。
5. pipeline owner 只借出自己的 immutable canonical shader facts，不复制/缓存 Graph rows。
6. semantic-free arena page/capacity 仍可由现有 pool 回收，但 page 内容不具有跨 invocation 语义。

#### 禁止

- topology/template/frozen pass；
- “静态 pass 部分”跨帧缓存；
- candidate/staged rows 再复制到 canonical rows；
- GraphId、view、access 或 shader argument 的跨 invocation 重用；
- backend 保存 authoring facts。

#### 必须证明

- row-by-row canonical equality；
- exact count 与实际 write count 双向断言；
- failure 不留下可执行的半填充 Graph；
- deterministic ordinal；
- author 与 E2E 真实 A/B。

### E-04 compiler 现有 CSR 的遍历收缩

#### 已知事实

当前 compiler 已经由 `AnalyzeLiveness` 生成并复用：

- `accessPassOrdinals`；
- `resourceAccessOffsets`；
- `resourceAccessOrdinals`。

这些索引已被 resource-indexed dependency/barrier compilation 与 transient placement 消费。因此
“再引入 CSR”不是新优化，不能重复计算。

仍可实验的具体删除项：

1. 在 liveness/index scan 中同时完成 queue-independent access validation，删除后续全 access 扫描。
2. queue rows 在 liveness 前一次 materialize，queue-dependent validation 只遍历 active-pass CSR。
3. descriptor/push-constant/pass lookup 的 count 与 active-pass scan 合并。
4. 热循环只借局部 `Span`/`ReadOnlySpan`，避免 Debug MinOpts 下重复
   `ArenaSlice<T>` indexer/`FindChunk`。
5. resource-independent hazard/barrier count/fill 可按 resource ordinal 固定分区并行；
   prefix sum 决定最终 canonical output ordinal。
6. placement 只按 immutable profile group 分区；组内 lifetime/alias order 继续使用 resource ordinal
   tie-break。
7. execution lowering 直接写最终 `CommandUnit`/task/batch rows，不新增 compiled Graph owner。

#### 必须证明

- reference compiler 与 candidate 的 canonical dependencies、barriers、placements、units、tasks、
  batches、external waits 全量 differential；
- scratch 全部由当前 invocation arena 拥有并随 Graph 销毁；
- 没有第二套 row lifecycle；
- 无 Dictionary/HashSet/PriorityQueue/LINQ 回流热路径；
- 每个被声称删除的 traversal 有计数证据；
- compiler 与 E2E 真实 A/B。

### E-05 barrier interval 与 native call 收缩

#### 已知事实

- 当前默认 Graph 有 343 条 logical barriers。
- command encoding 是最大单个分段热点。
- 不能假设旧文档中的静态 343 → 266 或 85 → 52 结果仍适用于当前终态源码。

#### 实验

1. 从当前 canonical barriers 重新统计实际 native enhanced-barrier group/call 数。
2. 只在 resource、before/after state、flags、queue、producer/consumer ordering 全部相同时合并。
3. texture 只合并能表示为 exact contiguous mip/layer/plane rectangle 的 cells。
4. 相邻 barrier units 只有在中间没有 GPU command 且 dependency coverage 相同时才合并。
5. 输出仍写回本 invocation 的 canonical barrier/unit rows；D3D12 只消费最终 rows。

#### 必须证明

- reference barrier 到 candidate barrier 的全覆盖映射；
- 无 missing/extra coverage；
- final resource states、alias edges 与 queue ownership 等价；
- D3D12 debug layer 与 GPU validation 的独立非计时运行无错误；
- frame output hash 等价；
- logical/native call count 与 encoding/E2E 真实 A/B。

### E-06 Runtime scene/extract/prepare 稳态收缩

#### 已知事实

- journal 已删除，但 scene + extract p50/p95 仍为 0.32/0.44 ms。
- prepare p50/p95 仍为 0.29/0.37 ms。
- Graph 外部分单独 p95 为 1.4522 ms。
- 当前 extraction 已有 source-slot → mirror-slot 映射与 transform-only direct apply，不能把这些现有
  机制再次写成未来收益。

#### 实验

1. 精确测量 `World.ExecuteQuery`、chunk writable detach、motion math、transform-only apply、
   `TryBeginPrepare`、timeline claim/release、instance write 与各 prepare system 的调用/分配。
2. 只删除已证明重复的 version/query/index/ownership check。
3. 对连续 chunk 值写使用 scoped span borrow，owner 仍是 World/chunk。
4. prepare 的 unchanged path 仍必须消费同一 timeline obligation；只能压缩重复 lock/lookup，
   不能跳过 pending submission。
5. motion 与 extraction 的 source/current/previous 值必须逐实体等价。

#### 禁止

- 使用 future-frame transform；
- 将 render mirror 变成第二个 game-world owner；
- 保留跨帧 command/Graph facts；
- 跳过 prepare/timeline obligation；
- 近似 `sin`、降低动态实例数或改变动画输出；
- 把 prepare 移到 admission 外。

#### 必须证明

- 1,024 entities 的 source、mirror、previous/current transform differential；
- chunk/World owner 与 serialization 行为测试；
- timeline pending/retry/failure 路径测试；
- scene/extract、prepare 与 E2E 真实 A/B。

### E-07 canonical batch schedule 的 CPU transaction 收缩

#### 已知事实

- 当前真实 schedule 为 4 个 submission batches。
- async compute 和真实 compute Queue 必须保留。
- 直接改成一个 submit 会破坏跨 queue 依赖，禁止。

#### 实验

从当前 resource-indexed dependency graph 重新计算：

1. 哪些相邻 same-queue batches 可在不新增 wait、不反转 producer/consumer、不改变 resource state 的
   条件下合并；
2. 哪些 pass 必须留在 compute queue；
3. 合并后每个 batch 的 exact command task rows、dependency rows、resource rows 与 external waits；
4. QueuePosition signal 数与 DevicePosition 合并结果。

不预设一定是 `4 → 3`；只有当前终态源码上的完整 differential 与真实 A/B 才能决定候选 schedule。

#### 必须证明

- canonical command order 与所有跨 queue happens-before；
- QueuePosition/DevicePosition 等价映射；
- 不新增 queue、generation、同步值或 wrapper；
- GPU timestamp 不出现新的 frame-admission blocking；
- 输出 hash 与 diagnostics 等价；
- submit 与 E2E 真实 A/B。

### E-08 critical-path 联合闭环

只有 Q-01、Q-02 和 E-01～E-07 中已经独立取得资格的项目才能进入联合候选。

联合候选必须：

- 冻结 exact source patch、assets、shaders、DLL/PDB hashes；
- 保持同一个完整 Default Runtime；
- 不按 benchmark mode 选择产品 fast path；
- 只允许 outer-only 模式改变计时探针，不改变执行；
- 使用单次使用 Graph；
- 每帧重写全部 canonical facts；
- 保留真实 Cluster callbacks、UI、async compute、D3D12 与 FIFO Present；
- 在联合运行先达到五进程 `<1 ms`，随后才做固定 ablation；
- 联合失败时不得临时添加第九个优化或扩大架构例外。

## 7. 候选晋级规则

一个实验只有同时满足以下条件，才能从 `E-*` 升级为 `Q-*`：

1. 完整 Default Runtime、真实 D3D12 单项 A/B；
2. 至少两次 candidate 与一个相邻 control，方向一致；
3. 明确证明删除了哪段具体工作，而不是只看总时间波动；
4. canonical facts、output、resource state、queue position 与 owner lifecycle 正确；
5. Graph before/after snapshot 或允许差异的全覆盖映射；
6. 没有采用 Section 2 和 Section 8 的禁止项；
7. Debug、`Optimize=false`、tiering/R2R 关闭；
8. build 与定向 tests 通过；
9. patch 与原始 CSV/trace/hash 可追溯；
10. 与已经取得资格的 Q 项联合运行没有撤销其效果或正确性。

单项晋级不表示最终方案 GO。只有冻结联合候选先通过最终五进程证书，才产生正式实现清单。

## 8. 明确排除

无论局部结果多快，以下做法一律失败：

- reusable `RenderGraph`；
- Graph template、frozen pass、compiled topology/cache；
- command replay/cache；
- 跨 invocation 保存 rows、GraphId、view、access、placement、barrier、task 或 batch；
- backend-owned Graph facts；
- 第二套 Graph model/shadow rows；
- 将 transient physical owner 从 Device 转给 Graph；
- 新增 frame-resource generation；
- 使用 future-frame scene、transform、upload 或 command；
- benchmark-only product fast path；
- Null backend、empty callback、synthetic pass 替代真实 Default Runtime；
- 降低分辨率、实例数、pass、shader、材质、UI、async compute 或输出质量；
- 关闭 FIFO Present；
- 4 → 1 submit；
- 第二 graphics/present queue；
- 新同步 token/future/packet/result/plan/frame wrapper；
- busy spin、无限制线程数、thread-pool flood；
- 把工作移到 admission 外；
- 把 component p95 相加后宣称通过；
- 用 `Optimize=true`、tiered compilation 或 ReadyToRun；
- 选择最好的一次运行；
- 在产品代码或 CI 中写入 `<1 ms` 机器门槛。

## 9. 正确性与架构证书

性能采样与正确性运行分开。冻结联合候选必须证明：

1. 1,024 动态实例 source/mirror/previous/current transform 等价；
2. camera、UI、scene、shader、material、pipeline identity 等价；
3. canonical Graph facts 等价，或每个允许结构变化有全覆盖映射；
4. dependency、barrier coverage、placement、command order、batch waits、QueuePosition 与 final states
   等价；
5. candidates、visible clusters、shaded pixels、deform cache 等 diagnostics 健康；
6. 固定输入序列 frame output readback hash 等价；
7. 多次 invocation 间不存在 Graph facts、compiled rows、handles 或 command replay；
8. D3D12 debug layer 与 GPU validation 独立运行无错误；
9. 每个 native/lifecycle obligation 恰好一个 owner；
10. borrow API 不复制、不保存 caller storage；
11. diagnostics 只拥有 detached immutable Snapshot；
12. structure gates 不出现被删除的 token/result/context/plan/packet 等平行模型；
13. 所有同价顺序使用 canonical ordinal tie-break；
14. correctness 与 performance binary 的源码、assets、shaders、编译输入 hash 一致。

## 10. 最终性能证书

每个进程使用：

```powershell
$env:DOTNET_TieredCompilation='0'
$env:COMPlus_TieredCompilation='0'
$env:DOTNET_ReadyToRun='0'
$env:COMPlus_ReadyToRun='0'
dotnet src/SomeEngine.Runtime/bin/Debug/net10.0/SomeEngine.Runtime.dll `
  --benchmark-output <raw.csv> `
  --benchmark-warmup 8192 `
  --benchmark-samples 16384 `
  --benchmark-outer-only
```

证书必须记录：

- OS、CPU、GPU、driver、.NET SDK/runtime、电源计划；
- process affinity/priority；
- 所有自有 DLL 的 `DebuggableAttribute.DisableOptimizations`；
- DLL、PDB、source patch、shader、asset、manifest、scene 的 SHA-256；
- Graph before/after snapshots 与 normalized diff；
- 5 个独立 CSV 的完整原始 samples；
- 每个进程独立的 p50/p95/p99/max/allocation/GC；
- admitted interval 内 wait、blocking wait、command allocation creation/reset 均为零；
- `timing_mode=outer-only`；
- component tick 列为零；
- 5 个进程各自 p95 均严格小于 1.000 ms。

任一进程失败，联合候选就是 `NO-GO`。不得重跑后替换失败进程。

## 11. 实施顺序与停止条件

这不是允许先实现全部假设的清单。每次只开放一个最小实验 patch：

```text
精确归因
→ 单项真实 A/B
→ 正确性 differential
→ 架构审计
→ 晋级或撤回
→ 与现有 Q 项联合复测
```

推荐按当前关键路径证据依次处理：

1. E-01：先把剩余 allocation 与 coordinator/worker 阶段精确绑定。
2. E-02：删除“全部录制结束后才开始第一 submit”的串行空窗。
3. E-04：收缩现有 CSR 周围的重复 compiler traversal。
4. E-05：用当前 canonical barriers 重新证明可合并的 native work。
5. E-03：在 compiler/record 输入稳定后，直接填充 canonical authoring ranges。
6. E-06：把 Graph 外 p95 从 1.4522 ms 的剩余工作逐一归因并收缩。
7. E-07：只在当前依赖图证明合法时减少 submission transaction。
8. E-08：冻结已晋级集合，执行联合正确性与五进程性能证书。

任何时刻若满足以下任一条件，停止当前实验并撤回：

- 需要违反 Section 2；
- 只能在 benchmark mode 生效；
- 不能给出 canonical/output/owner differential；
- 两次 candidate 与相邻 control 方向不一致；
- 删除的工作只是被移动到 measured interval 外；
- 联合候选没有通过五进程 `<1 ms`；
- 需要临时添加未冻结的新机制才能“再快一点”。

## 12. 当前裁决

| 条件 | 状态 |
|---|---|
| 目标、场景、测量边界冻结 | PASS |
| 指定架构读取并形成硬约束 | PASS |
| Debug `Optimize=false` 真实基线 | PASS |
| ordinary Execute outer-only 路径 | PASS |
| Q-01 局部因果证据 | PASS |
| Q-02 短样本局部因果证据 | PASS，定向测试已通过；仍需长样本与完整解决方案验证 |
| R-01/R-02/R-03 否决并撤回 | PASS |
| 剩余实验单项资格 | 未完成 |
| 冻结联合 correctness | 未完成 |
| 五个独立进程 p95 `<1 ms` | 未完成 |
| 正式实现 GO | **NO-GO** |

当前最重要的事实不是“还差一个开关”，而是：相同当前源码的两次普通 Execute 真实短样本 p95
分别为 **7.5991 ms** 和 **11.8865 ms**，且所有 4,096 个 samples 都大于等于 1 ms。本文件因此
不伪造 `<1 ms` 可达结论；它把达到该结论所需的每一条证据、架构边界、实验晋级规则和最终证书
固定下来。
