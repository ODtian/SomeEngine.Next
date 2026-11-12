# ECS 与物理修改计划（2026-11-08）

本页列出 ECS 重写版接入与物理改造的全部修改项。每项写明当前代码位置、目标依据、具体修改、验收方式，以及与文档的一致性，用于逐项判断是否符合设计。

## 0. 依据与判定方法

### 0.1 设计来源与版本

| 记号 | 来源 | 版本 |
|---|---|---|
| AG | 根级 `AGENTS.md`（代码风格、Validation、Testing、Rendering） | main `1078244a` |
| RB/页名 | `wiki/architecture/ECS/Rebuild/*.md`（ECS 重构规范） | `origin/codex/ecs-rebuild-review` `d42c3add` |
| EW/页名 | `wiki/architecture/ECS/*.md`（现行 ECS Wiki） | main `1078244a` |
| PW/页名 | `wiki/architecture/physics/*.md`（物理 Wiki） | `origin/codex/physics407-integration` `25005179` |
| S407 §n / 故事 n | #407 规格正文 | 2026-11-08 状态 |
| ADR nnnn | `docs/adr/` | main |
| BC | `wiki/harness/Behavior-Checks.md` | main |
| D1–D8 | 本会话中已确定的决策，见 0.2 | 2026-11-08 |

源码位置标注规则：
- main 的文件用相对链接。
- 分支上的文件写成 `分支:路径` 形式。ECS 原型分支记作 `ecs:`（`origin/codex/ecs-codec-count` `2510b54d`），物理分支记作 `phy:`（`origin/codex/physics407-integration` `25005179`）。

### 0.2 已确定的决策

| 编号 | 决策 | 影响的文档 |
|---|---|---|
| D1 | 撤销接触点的实体身份。逐点接触数据由物理持有，按形状对缓存，并为游戏提供接口 | S407 §2 第 3 条、故事 14；PW/Contacts（需修订，见 §9） |
| D2 | 只读视图、事件流、修改阶段做成引擎通用的结果交付机制，物理接触只是其中一个使用方 | 新 ADR（§9） |
| D3 | 不使用回调。确需扩展点时，以泛型特化的 struct 表达。接触修改就是一个普通阶段，不另设规则接口和组合类型 | 符合 AG 的 Validation 与代码风格要求 |
| D4 | CPU/GPU 由各阶段的执行者持有数据，只在执行者交接处传输，允许多套配置并存 | 符合 S407 §7 |
| D5 | 配对查询引擎（Within/Between/Product/Where/Except 及空间摘要）迁入新 ECS | 符合 S407 故事 3、39 与 EW/Relation-Queries |
| D6 | 新的公开接口不提供委托重载 | 符合 D3 |
| D7 | 普通 `Playback` 保留成功前缀并发布对应成员变化；显式原子流程在候选 World 上回放，成功后交接，失败时保留源 World。现有整批回滚行为在显式原子流程中验收 | RB/Commands；§2.7、§4.3.2（用户于 2026-11-08 确认） |
| D8 | 性能基线采用正常使用的目标和公开参考实现；普通性能运行首个有效采样低于认可的目标速度时立即结束进程，显式定位按明确诊断范围运行；Debug/Release 构建配置均执行普通测量门槛 | §0.4、§7.7、§8（用户于 2026-11-08 确认） |

### 0.3 已确认的实施选择

用户于 2026-11-08 确认统一采纳 P1–P6 的建议。下表作为实施决策，正文中的"依赖 Pn"按本表执行。

| 编号 | 问题 | 已确认的实施选择 |
|---|---|---|
| P1 | #447 的合入门槛 | 以行为测试和实际消费者负载不回退为门槛。880 行"逐项领先四个参照"作为后续优化目标，不阻塞接入（需修订 RB/Acceptance 的门槛表述） |
| P2 | 事件日志的读取者长期不前进时怎么处理 | 日志扩容并计入资源账；连续 N 步不前进时由 validation 层报告；不静默丢弃 |
| P3 | 读取者开始登记之前已发布的事件，它能否看到 | 只能看到登记之后发布的事件，与 RB/Changes 的"注册点是当前尾序号"一致 |
| P4 | 事件日志与步结果视图放在哪个项目 | 放在 `SomeEngine.Job`，它只依赖资源令牌和 `JobHandle` |
| P5 | 空间索引的实现（均匀网格、BVH）放在哪一层 | 接口和均匀网格、BVH 放进新 ECS 的 Relationships；碰撞专用的组织方式留在物理 |
| P6 | 接触改造放在新 ECS 接入之前还是之后 | 之后，放在 #442 改为基于新 ECS 的那一步 |

### 0.4 判定方法

每个修改项用下面的格式记录：

- **当前**：源码位置和现状。
- **依据**：对应的文档条目。
- **修改**：文件、类型、签名和删除项。
- **验收**：公开行为测试或测量命令。
- **一致性**：分三种。
  - "符合"：与列出的文档条目一致。
  - "需修订 X"：文档要随这次修改一起改，修订内容列在 §9。
  - "新增"：文档里还没有对应条目，需要补写。

所有新增类型名都按 AG 的禁用词列表检查过。

DefaultGame 标准三场景统一使用 Release NativeAOT、Direct3D 12、1280×720、4 个物理核、16 GiB 进程树额度和 512 MiB 几何页堆。各场景使用 128 帧预热与 128 帧采样，整轮上限 60 秒；普通测量逐个有效采样检查 `1000d / 60` ms 目标。实际命令、资源额度、场景选项和退出状态随结果保留；详细捕获作为独立诊断配置记录。

---

## 1. 阶段 0：前置条件

| 编号 | 修改 | 依据 | 验收 | 一致性 |
|---|---|---|---|---|
| 0.1 | 用户于 2026-11-08 确认跳过 CI 门槛；全部工作流只保留 `workflow_dispatch`，提交、PR、定时和工作流事件均改为手动触发。仓库保持 Public | 用户本次指示 | 工作流触发器只有手动入口；合入沿本地构建、行为测试与场景测量验收 | 符合用户本次修订 |
| 0.2 | 解决 #405 与 main 的冲突后合入 | #447 评论中的合入顺序 | 本地构建与行为测试通过 | 符合 |
| 0.3 | 把 #406 的 base 改为 main 后合入 | 同上 | 本地构建与行为测试通过；`tests/SomeEngine.Job.Tests` 通过（main 现有 173 个测试） | 符合 |
| 0.4 | 基线采用正常使用的性能目标与公开参考实现，直接用于重构后的验收。场景范围：MillionPhysicsActivity 原六帧、pyramids-88200 前 3 步、silo 10k 64 步、角色 2k/5k/10k、DefaultGame 三场景。普通性能运行在预期速度检查点低于目标时立即结束进程；debug 定位按独立诊断范围运行。构建与测量通过 `tools/Invoke-Benchmark.ps1` 提交 | 用户于 2026-11-08 修订；AG Testing、`wiki/harness/Harness-Performance-Evidence.md` | 核对规定行为、物理整步预算和 ECS 公开参考；保留实际验收的 stdout、stderr、退出码与配置 | 符合用户本次修订 |

## 2. 阶段 1：Job 合并

| 编号 | 当前 | 修改 | 依据 | 验收 | 一致性 |
|---|---|---|---|---|---|
| 1.1 | main 的 `src/SomeEngine.Job` 自 `13864df6` 以来没有变过；原型版本在 `ecs:Job/`，相对 main 是 +855/−113 | 以 `13864df6` 为基，把 `ecs:Job/` 的差异作为补丁打到合入 #406 之后的 main 上。只有 6 个文件会冲突：`JobWorkStreams.cs`、`Scheduler.Execution.cs`、`Scheduler.ExecutionStore.cs`、`Scheduler.Runtime.cs`、`SynchronousResourceOwner.cs`、`WorkQueue.cs` | RB/Data-and-Dependencies 的"程序集与外部依赖"一节：Job 增加泛型同步范围入口，以及依赖完成后的分阶段访问准备 | Job 测试全部通过 | 符合 |
| 1.2 | #406 有 `ExecuteParallel`；原型有 `ExecuteRanges<TWork, TItem>`（`ecs:Job/Scheduler.Ranges.cs`）和 `IRangeWork<TItem>` | 以 `ExecuteRanges` 为核心；`ExecuteParallel` 作为它的外层，负责取得资源和确定批大小 | RB/Jobs 的"D 同步批次"与"批次权限与单范围执行" | Job 测试；原型 Comparison 的 job-execute 组不回退 | 符合 |
| 1.3 | `phy:src/SomeEngine.Job/ResourceManager.AccessRanges.cs`（提交 `98b983f0`，按读写模式取实际区间的并集） | 并入本阶段：资源登记按资源和读写模式合并相邻、交叠的区间，保留区间之间的空隙 | #444 的"大批设备范围声明"一节；ADR 0002 | 新增公开行为测试：相邻与交叠区间按顺序执行、不交叠的区间并行、读写模式分开 | 符合 |
| 1.4 | 物理分支上与 #406 共有之外的其余 Job 提交（类别配额、同步范围、追踪） | 和 #406 已有的内容逐项去重后，并入本阶段 | 同 1.1 | 同上 | 符合 |
| 1.5 | 结构写入在非 Job 线程上每次调用都要走一次调度器的完整准许流程（`phy:src/SomeEngine.ECS.Systems/WorldJobs.cs` 的 `EnterCore` → `Scheduler.AcquireSynchronousAccess` 建立 Job 状态、登记、等待、释放） | 提供"批量结构编辑只准许一次"的入口：结构写入的范围由调用方显式打开，范围内所有编辑共用一次准许 | RB/Implementation"借用签名与普通值写入"一节中，范围入口在独立调用时取得、最终释放访问 | Job 基准里加一行：N 次单实体 Add 对比一次批量编辑；场景建立测量见 6.9 | 符合（是 RB 已有的范围寿命规则） |

## 3. 阶段 2：ECS 核心移植

| 编号 | 当前 | 修改 | 依据 | 验收 | 一致性 |
|---|---|---|---|---|---|
| 2.1 | 原型项目名是 `*.Validation.csproj`（`ecs:Core/SomeEngine.ECS.Validation.csproj` 等），与 main 没有共同历史 | 按源码移植，不做 merge。项目对应关系：`Core` → `src/SomeEngine.ECS`，`Relationships` → `src/SomeEngine.ECS.Relationships`，`Systems` → `src/SomeEngine.ECS.Systems`，`Serialization` → `src/SomeEngine.ECS.Serialization`，`Generators` → `src/SomeEngine.ECS.SourceGen` | RB/Data-and-Dependencies 的"程序集与外部依赖"与"目录和具体文件" | 整个解决方案能编译 | 符合 |
| 2.2 | `ecs:Diagnostics/DevelopmentCheck.cs` 与 main 的 `src/SomeEngine.Diagnostics/DevelopmentCheck.cs` 重复 | 删除原型里的这一份，引用 main 的 | AG Validation（统一的 validation 层） | 编译通过 | 符合 |
| 2.3 | 原型里有自己的 `ecs:Serialization/IO`（命名空间 `SomeEngine.Serialization`），main 已有 `src/SomeEngine.Serialization` | 合并进 main 的 `SomeEngine.Serialization` | RB/Data-and-Dependencies：Serialization 提供基础字段编码、分段读写和文件服务 | Serialization 测试 | 符合 |
| 2.4 | `ecs:Comparison/` | 移到 `benchmarks/SomeEngine.ECS.Comparison` | AG Testing（基准测试走公共队列） | 能经 `Invoke-Benchmark.ps1` 运行 | 符合 |
| 2.5 | Core 的 `InternalsVisibleTo` 指向三个 `*.Validation` 程序集 | 改为指向正式的 Relationships、Systems、Serialization 程序集，不指向任何测试程序集 | RB/Implementation 的"程序集可见性与生成代码"；BC（禁止对测试程序集暴露内部成员） | `tools/Check-TestRules.ps1` 和 ArchitectureRules 通过 | 符合 |
| 2.6 | main 的 ECS/Job 目录下有 45 个文件名含禁用词；原型有 8 个（`JobRuntimeConfig`、`RuntimeCounters`、`Scheduler.Runtime`、`SynchronousResourceOwner`、`ExternalReferenceContracts`、`JobExecutionContext`、`JobRuntimeStats`、`Job.Contracts`） | 新增类型一律不使用禁用词。原型中既有的这 8 个名字列入改名清单，在本阶段一并改掉 | AG 代码风格 | ArchitectureRules 加一条禁用词检查 | 符合 |
| 2.7 | 语义基准：main 的 `tests/SomeEngine.ECS.Tests`（536 个测试）、`ECS.Systems.Tests`（242）、`ECS.Relations.Tests`（49）、`ECS.Serialization.Tests`（142） | 测试的行为期望按已定 RB/D7 对齐，签名按新接口改写。拓扑维护差额通过 Preserve 往返保留主值与 Applied 顺序，显式 Maintain 后交付新端点或父链；旧默认保存拒绝差额的门槛由该恢复行为替代。存档写入统一使用 BeginWrite/Commit，受限 payload stream 验证同步写入与发布边界。同步 Save 在读范围内编码；并发修改源 World 使用显式 Capture 后编码，普通 managed 引用浅复制、buffer 深复制；原 COW 隔离断言按该已定 RB 行为对应迁移。Views 的业务节点存在以 ViewType 及其查询成员判断，Removed/cleanup 保留身份的生命周期由 Core 验收；焦点在实际输入消费期读取父关系移除事实。查询成员日志只跟踪实体集合进出，首次遍历按公开 query 顺序核对；无关实体删除引起的 chunk 顺序变化沿用已捕获视图行，实际 Disable/Enable 进出刷新 keyed 行。组件写版本沿 World 的唯一 ulong 序列观察；旧值事实由 IRemovedComponent 显式声明。版本的动态入口以 nullable 表达组件缺失，实际值读取保留实体与组件检查。拓扑写入与维护共用 World 结构范围，同 World 顺序由依赖表达，不同 World 保持独立执行。对每个删除的测试，在 PR 里写明它对应的行为由哪个新测试覆盖，或者该行为已被决策撤销 | BC；`docs/agents/domain.md`（当前行为以源码为准） | 全部通过；删除清单进 PR 正文 | 符合 |
| 2.8 | 原型的 `World` 是 `public sealed partial class`（`ecs:Core/Changes/World.BatchVersions.cs`） | 保持 sealed。`RenderWorld` 改为持有一个 World 实例（见 4.7） | RB/Implementation（应用侧只使用 World、查询、视图和注册声明） | 编译通过 | 符合 |

## 4. 阶段 3：通用结果交付机制（D2）

### 4.1 API（新增，放在 `SomeEngine.Job`，依赖 P4）

```csharp
public sealed class EventLog<T> where T : unmanaged
{
    public EventReader<T> Watch();                   // 匿名读取者，不进存档
    public EventReader<T> Watch(Guid persistentId);  // 命名读取者，进度进存档
    public bool HasReaders { get; }
    // 生产者入口（Job 内、已声明写访问时使用）：prefix 阶段之后按总数预留连续区间
    public EventWriter<T> Reserve(int count);
}

public readonly ref struct EventWriter<T> where T : unmanaged
{
    public Span<T> Values { get; }                   // 预留区间，各工作项按自己的前缀偏移写入
}

public sealed class EventReader<T> : IDisposable where T : unmanaged
{
    public ulong Position { get; }
    public JobHandle Ready { get; }                  // 最近一次发布的完成点
    public JobResourceAccess Read { get; }           // 覆盖 [Position, Through)
    public EventRead<T> Open();
    public void Advance(ulong through);
}

public readonly ref struct EventRead<T> where T : unmanaged
{
    public ReadOnlySpan<T> First { get; }
    public ReadOnlySpan<T> Second { get; }
    public ulong Through { get; }
}

// 步结果视图：引擎只提供完成点与读取声明；视图本身由生产者以具体类型给出
public readonly struct StepAccess(JobHandle completed, JobResourceAccess[] reads)
{
    public JobHandle Completed { get; } = completed;
    public ReadOnlySpan<JobResourceAccess> Reads => reads;
}

// 生产者的写法（以物理接触为例）
public sealed class PhysicsContacts
{
    public StepAccess Access { get; }                // 本步完成点与读取范围
    public ContactView OpenView();                   // 声明 Access.Reads 并依赖 Access.Completed 后调用
}
```

视图不做成泛型容器：引擎无法在不持有生产者引用、也不经过接口分派的情况下构造具体的 ref struct 视图。所以引擎只统一"完成点 + 读取声明"这部分（`StepAccess`），`OpenView()` 由每个生产者以具体类型提供。

### 4.2 修改项

| 编号 | 修改 | 依据 | 验收 | 一致性 |
|---|---|---|---|---|
| 4.2.1 | 存储与回收算法直接沿用 RB/Changes"销毁序列"一节：连续数组；只推进 Start；空间不够时，把仍需保留的项一次搬到起点，再不够就扩容；回收取各读取者进度的最小值 | RB/Changes | 公开行为测试：多个读取者各自推进；最慢的读取者推进之后才回收；扩容前后内容不变 | 符合（把已有算法推广为通用类型） |
| 4.2.2 | 发布：`Through` 的推进是生产者完成链里的一步，`Ready` 对应这个完成点。读取者声明 `[Position, Through)`，生产者写 `Through` 之后，两段范围不重叠，可以并行 | ADR 0002；RB/Jobs 的"资源、依赖与系统图" | 用显式同步点证明顺序：生产者写入第 n+1 步的同时，读取者读第 n 步 | 符合 |
| 4.2.3 | 并行写入采用 count/prefix/write 三步，每个事件不需要原子操作；序号顺序由计数顺序决定 | ADR 0004（确定的结构顺序） | 两次运行得到相同的事件序列 | 符合 |
| 4.2.4 | `HasReaders == false` 时，生产者跳过事件的产生和回读 | D2（按需付费） | 公开测试：没有读取者时 `Watch` 之前的步不产生事件（依赖 P3） | 新增 |
| 4.2.5 | 命名读取者的进度和尚未消费的区间进入存档 | RB/Serialization 的"销毁消费者的稳定对应"；ADR 0005 | 存档往返后读取者继续读到相同的序列 | 符合 |
| 4.2.6 | validation：未声明读取就 `Open`、`Dispose` 后继续使用、读取者长期不前进（P2），都由 validation 层检查，运行时可关闭 | AG Validation | 只断言检查被触发（BC） | 符合 |
| 4.2.7 | GPU 生产者：日志放在设备 buffer 里；只有登记了 CPU 读取者时才安排回读，且只回读已发布的区间；完成点沿外部 fence 链 | S407 §7；D4 | 设备事件的行为测试：没有 CPU 读取者时不提交回读 | 符合 |

### 4.3 ECS 作为生产者

| 编号 | 当前 | 修改 | 依据 | 验收 | 一致性 |
|---|---|---|---|---|---|
| 4.3.1 | `ecs:Core/World.Lifecycle.cs` 的 `DestroyedReader` / `DestroyedRead` | 改为 `EventLog<DestroyedEntity>` 的实例，`World.WatchDestroyed()` 的签名保持不变 | RB/Changes | 原型和 main 的销毁事实测试 | 符合 |
| 4.3.2 | main 的 `EntityTableChanges`（[EntityStore.Changes.cs](../src/SomeEngine.ECS/Entities/EntityStore.Changes.cs)）：全局锁，每个观察者各自用 `ImmutableDictionary` 逐实体登记 | 删除，改为按查询的成员进出日志 `World.WatchMembership(Query)` → `EventReader<MembershipChange>`。实现方式：每个表缓存"有读取者且与该表匹配的查询"列表；结构提交时，按批次把实体列复制进对应日志 | EW/Change-Tracking 的"查询成员观察版本"（`MembershipVersion`）；ADR 0003（日志从原生事实派生，原生的 Added/Changed/Removed 仍然存在）；D7 | #443 的行为改写为成员日志版本：较早批次被确认后，后续变化和销毁仍保留；显式原子流程失败时保留源 World 及其成员日志；普通 Playback 发布成功前缀的成员变化；各读取者独立前进 | 需修订 EW/Change-Tracking（新增成员日志一节） |
| 4.3.3 | main 的 `World.EntityDestroying` 事件（[World.Entities.cs:12](../src/SomeEngine.ECS/World.Entities.cs)） | 删除，所有使用方改为 4.3.1 | D3；#445 | #445 的验收（见 5.6） | 符合 |

## 5. 阶段 4：接口补齐与调用方迁移

| 编号 | 当前 | 修改 | 依据 | 验收 | 一致性 |
|---|---|---|---|---|---|
| 5.1 | 旧查询接口 `QueryRow`、`QueryCursor`、`QueryChunkView`，以及委托形式的 `ExecuteQuery(..., static (scoped QueryCursor ...) => ...)`：物理用了 485/342/39 处，渲染、Views、导航也在用 | 不在新 ECS 里重做这几个类型。调用方改用 `Query.ForEach<..., TAction>` 等 `IComponentAction` 形式（`ecs:Core/Queries/Query.Components.cs`、`Query.Chunks.cs`）。需要按实体随机访问的，改用 `OpenRead<T>()`/`OpenWrite<T>()` | RB/Implementation 的跨层接口表与借用签名；D6 | 各调用方的现有行为测试 | 需修订引用旧查询类型的 EW 页面：Active-Owner-Borrow-Creation、Bitmap-Relation-Queries、Change-Tracking、Component-Bytes、Component-Fields、Relation-Packages-and-AOT、Relation-Queries、Spatial-Filtering、Spatial-Maintenance、Spatial-Parallel-Queries、Storable-Descriptions、Systems |
| 5.2 | 配对查询引擎在 main 的 `src/SomeEngine.ECS.Relations`（物理分支上 32 个文件、约 69 KB）以及 6 个生成器 | 迁入 `SomeEngine.ECS.Relationships`，命名空间仍为 `SomeEngine.ECS.Relations`。`IQueryRelation<TRow>.Matches(in row, QueryRow, QueryRow)` 改为按 chunk 绑定的列和行下标；基于存储边的来源改为绑定 RB/Relations 的边存储；分区计数改走 `ExecuteRanges` | D5；EW/Relation-Queries、Key-Relation-Queries、Bitmap-Relation-Queries | `tests/SomeEngine.ECS.Relations.Tests`（49 个）全部保留 | 需修订 RB/Data-and-Dependencies（Relationships 一行加上配对查询） |
| 5.3 | `SpatialCandidates`、`CollisionTree`、`CollisionGrid` 在 `src/SomeEngine.Physics` | 依赖 P5：空间候选接口和均匀网格、BVH 迁到 Relationships；碰撞专用的部分（预测范围、分区 SAP 的碰撞组织）留在物理 | EW/Spatial-Candidates、Spatial-Filtering、Spatial-Maintenance | EW/Spatial-* 对应的测试 | 依赖 P5 |
| 5.4 | 物理在旧 ECS 上新增的能力：`EntityQueryJobs*`、`EntityQueryViews`、`QueryReadResources*`、`ComponentWriteJobs`、`ComponentResourceGroups`、`ReadOnlyQueryTripleJobs`、`World.ReadQueries`、`CommandBuffer.ComponentAdds`、`CommandBuffer.RelationCreates`、`BufferRead/WriteColumn`（`phy:src/SomeEngine.ECS*`，共 98 个文件） | 逐项映射到新 ECS。实体分组 Job 映射到 `ExecuteRanges` 加查询范围；按实体读取映射到 `OpenRead`/`OpenWrite`；同一批命令内引用新建实体映射到原型的 `ReserveCreates`/`DeferredEntity`；资源区间登记映射到 1.3。没有对应物的项要在 PR 中说明原因 | #444；RB/Commands 的"载荷中的临时实体引用" | 物理的行为测试（见 §7） | 符合 |
| 5.5 | `IJobComponentResources<T>.Collect` 逐行、逐组件登记资源（[JobComponentResources.cs:25](../src/SomeEngine.ECS.Systems/JobComponentResources.cs)） | 改为按 chunk 范围登记；同一资源的相邻行由 1.3 合并 | #444 | 1,000,001 个成员的"成员捕获结束到写作业开始"区间重新测量（原为 12,586.9 ms，Debug） | 符合 |
| 5.6 | Views 的 `BuildQueue` 订阅 `EntityDestroying`，在销毁过程中读取 Sparse 组件 `Computations`，而且每销毁一个实体都线性扫描一遍 `_computations`（[BuildQueue.cs:33](../src/SomeEngine.Views/BuildQueue.cs)、第 72 行） | 改为 `WatchDestroyed()` 读取者，在 Views 自己的 Job 里声明 `Sparse<Computations>` 的读写后再清理；`_computations` 改为按实体建立索引 | #445；ADR 0010 | `tests/SomeEngine.Views.Tests`；#445 的应用命令 `DefaultGame --physics=character-cloth --frames=3`，退出码 0 并输出图像 | 符合 |
| 5.7 | `RenderWorld : World`（[RenderWorld.cs:21](../src/SomeEngine.Render/Systems/RenderWorld.cs)） | 改为 `public sealed class RenderWorld { public World World { get; } ... }`，渲染系统通过 `.World` 访问 | AG Rendering（Game world 与 Rendering world 分离） | `tests/SomeEngine.RenderGraph.Tests` 及渲染相关测试 | 符合 |
| 5.8 | 有 6 处使用 `ObserveEntityTables`：`RenderExtractionSchedule.cs:158`、`RenderInstanceBatchBuilder.cs:261`、`RenderLightSetSystem.cs:52`、`ClusterMeshPreparation.cs`、`ClusterMaterialAssignments.cs`、`Physics/CollisionMaintenance.cs:70` | 各自改为对自己查询的 `WatchMembership(query)`。渲染提取只在源查询的成员发生变化时才进入完整对账路径 | AG Rendering（提取仍然是唯一的数据通路，成员日志只在提取内部读取） | DefaultGame 三场景的行为和帧输出；物理场景运行时渲染提取不再每帧完整对账 | 符合 |
| 5.9 | 渲染提取用 `ExecuteReadSnapshot` 在回调期间占住整个拓扑 | 改为新 ECS 的结构读取范围，在 Job 里声明访问 | RB/Implementation 的借用签名 | 提取的行为测试 | 符合 |
| 5.10 | Navigation 的 `NavigationNeighbors` 用 `Dictionary<(X, Z, Layer), int>` 自建均匀网格（`src/SomeEngine.Navigation/NavigationNeighborSystem.cs`） | 依赖 P5：改用配对查询 Within 加均匀网格候选 | D5 | `tests/SomeEngine.Navigation.Tests` | 依赖 P5 |
| 5.11 | 其余 ECS 使用方：Elements、Layout、Animation、Animation.Pose、Assets、Resources、Runtime、DefaultGame（约 800 个文件） | 按依赖顺序迁移：Views/Elements/Layout → Animation → Navigation → Assets → Render → Runtime/DefaultGame。每一步都要求整个解决方案编译通过、测试通过 | `docs/agents/domain.md` | 每步全量测试 | 符合 |

## 6. 阶段 5：存档版本 5

| 编号 | 当前 | 修改 | 依据 | 验收 | 一致性 |
|---|---|---|---|---|---|
| 6.1 | main 的存档格式是版本 4（ADR 0005）；原型 `ecs:Serialization/WorldSerializer.cs:13` 是 `FormatVersion = 5` | 按 RB/Serialization 的"版本 5 的字段顺序"实现；`DurableSaveStore` 封装格式仍为 4 | RB/Serialization | `tests/SomeEngine.ECS.Serialization.Tests` | 需修订 ADR 0005（版本 4 → 5） |
| 6.2 | `src/SomeEngine.Assets/WorldBlock.cs`、`NavigationSave*.cs` 使用 ECS 存档；渲染的 `SceneSerialization.cs` 使用 `ICanonicalComponentCodec` | 改用新 codec 接口；接口名不再含禁用词 | 同上；AG 代码风格 | Assets、Navigation 测试 | 符合 |
| 6.3 | 内容需要重建 | 用户于 2026-11-11 确认一并实现 #392（EntityAsset 追加）、#393（WorldStream 显式流送）和 #400（第一方目录/实体资产输出），然后统一重建内容 | #447 评论第 5 步；用户本次确认 | 内容构建与 DefaultGame 三场景 | 符合 |

## 7. 阶段 6：物理改造（在 #442 内完成，依赖 P6）

### 7.1 接触数据（D1）

| 编号 | 当前 | 修改 | 依据 | 一致性 |
|---|---|---|---|---|
| 7.1.1 | 接触点是一个关系实体（`phy:src/SomeEngine.Physics/ContactPoint.cs`，`[RelationSchema(Undirected)]`），每个点还有 2 个参与关系实体和一个 `ContactBodyLink` Buffer | 新增物理持有的按列存储 `ContactManifolds<TState>`（CPU）和 `DeviceContactManifolds<TState>`（GPU），列包括：形状对下标、双方特征、局部点、法向、间距、作用端，以及求解器状态 `TState`（如 `AvbdContactState`）。另有形状对表，键为（EntityA, EntityB, 形状 A, 形状 B），每步重建 | D1；S407 §4 第 3 条（"共同接触数据表达双方实体与特征……"仍然满足） | 需修订 S407 §2 第 3 条、故事 14；PW/Contacts |
| 7.1.2 | 跨步匹配：设备匹配结果回到 CPU，再回放成实体（`DeviceContactMatches*.cs`、`ContactPoints.cs` 两次回放） | 匹配留在设备上：以（形状对，特征）为键查上一步的列，热启动值直接写进本步的列，不再回到 CPU 建实体 | D1、D4；S407 故事 55（接触持久化、热启动） | 需修订 PW/Contacts 的"设备匹配与原实体回放" |
| 7.1.3 | 求解器附加状态通过 `IContactPointComponents.Add(ref JobCommandWriter, ...)` 以组件形式安装 | 删除。求解器的接触状态改为 `ContactManifolds<TState>` 的 `TState` 列；`AvbdContactState` 等仍是普通 struct | D3；S407 故事 7（用户实现和内置实现走同一入口） | 符合 |
| 7.1.4 | 需要删除或重写的文件：`ContactPoint.cs`（关系 schema）、`ContactPoints.cs`、`ContactPointComponents.cs`、`ContactIdentities*.cs`、`DeviceContactInputs.cs`、`ContactEvents.cs`、`ContactEventSources.cs`、`DeviceContactEventCounts.cs`、`InitializeContactResponses.cs`、`ContactStateInitialization.cs`、`XpbdContactComponents.cs`、`ContactLongRangeConstraints.cs`、`ContactLongRangeGraphConstraints.cs` | 按 7.1.1–7.1.3 和 7.2 重写；LRC 改为读取接触视图 | — | — |
| 7.1.5 | 游戏侧接口 | `PhysicsContacts.Access : StepAccess` 加上 `PhysicsContacts.OpenView() : ContactView`。`ContactView` 提供 `Pairs`、`PointsOf(pair)`、`PairsOf(Entity body)`（按刚体的 CSR 下标，每步重建） | D2；S407 故事 52 | 新增 |
| 7.1.6 | 接触修改 | 修改就是一个普通阶段：项目在检测与求解之间插入自己的 Job 或 kernel，声明对 `ContactManifolds` 的写访问；不设规则接口 | D3 | 新增（PW/Contacts 增加一节） |

### 7.2 事件

| 编号 | 当前 | 修改 | 依据 | 一致性 |
|---|---|---|---|---|
| 7.2.1 | `PhysicsEvents.cs` 每步建一个流实体，带 `PhysicsEvent` Buffer，再用命令确认（第 80–133 行）；`ContactEventState<TScope>` 是保留到销毁之后的 `ICleanupComponent` | 改为物理持有的 `EventLog<PhysicsEvent>`。接触的 Begin/Stay/End 由每步形状对集合的差异得出；只有任一方刚体带 `ReportContacts` 组件，而且日志 `HasReaders` 时才产生 | D2；S407 故事 52 | 需修订 PW/Events |
| 7.2.2 | 断裂、休眠、唤醒事件 | 进入同一个日志，`Kind` 字段保留 | PW/Events | 符合 |
| 7.2.3 | `IPhysicsEventConsumer` / `ConsumePhysicsEvents<TConsumer>` | 删除。消费者在自己的 Job 里用 `EventReader<PhysicsEvent>` 读取 | D3 | 需修订 PW/Events |

### 7.3 执行者交接（D4）

| 编号 | 当前 | 修改 | 依据 | 一致性 |
|---|---|---|---|---|
| 7.3.1 | `DeviceConstraintInputs`：先在发生变化的 chunk 里逐行 `Has`/`TryRead`，把实体收集进 `List<Entity>`，再按实体重新定位后逐行写入（`phy:.../DeviceConstraintInputs.cs:67, 102`） | 按 chunk 取列 span，整段复制进 staging；只有变化窗口内的 chunk 参与；资源按 chunk 区间登记 | S407 §7"必要的设备转换与传输具有明确用途与完成点"；RB/Storage-and-Queries 的"连续值视图" | 符合 |
| 7.3.2 | `DeviceBodyInputs` 逐行比较写版本 | 同 7.3.1，按 chunk 处理 | PW/Device-Bodies | 符合 |
| 7.3.3 | 角色场景选择全体刚体 CPU 消费（`DeviceBodyResults` 全量回读，写回 ECS） | 显示改走 #446 的设备路径；CPU 结果只回写游戏实际读取的实体。`DeviceBodyResults` 的接口不变，由项目自己选择范围 | D4；#446 | 符合 |

### 7.4 约束图

| 编号 | 当前 | 修改 | 依据 | 一致性 |
|---|---|---|---|---|
| 7.4.1 | `PhysicsGraph.cs` 第 40–56 行：`Dictionary<Entity, BodyInput>`、`Dictionary<PhysicsConstraintIdentity, ConstraintInput>`、多个 `HashSet<Entity>`，每个刚体和约束各一个对象 | 刚体按 `DeviceBodyBinding.Index` 稠密编号，用数组存储。关节等持久约束的连通关系从原生关系的 Added/Removed 增量维护（ADR 0003）。接触产生的岛每步从接触列直接重建 | AG（数据只有一个主要持有者）；S407 §7 第 2 条（岛从实际连接派生） | 需修订 PW/Graph-Solving |

### 7.5 建立

| 编号 | 当前 | 修改 | 依据 | 一致性 |
|---|---|---|---|---|
| 7.5.1 | `DevicePosePhysics.Bind`（`phy:src/SomeEngine.Animation.Pose/DevicePosePhysics.cs:89–130`）、`ConstraintBodies.Create`、`PhysicsCrowdScene` 构造函数：逐实体 `CreateEntity` 后再逐个 `Add`，每个角色约 380 次准许过的 World 调用 | 接口改为接收 span 和最终组件组合，按组件组合批量创建，在一次结构编辑范围内完成（1.5）。关系用 RB/Relations 的拓扑批次创建 | RB/Bulk-and-Copy 的"批量创建"；RB/Implementation 的拓扑批次 | 符合 |
| 7.5.2 | 原型的批量 `Create` 最多支持 6 个组件（`ecs:Core/World.Bulk.*.cs`），而刚体有 8 个组件 | 按 RB 补齐：7 至 10 类型的 `Create`/`Reserve` 重载，以及由生成器产出的组合创建（"组合与 Bundle 生成"：一次定位最终布局，逐字段写值） | RB/Bulk-and-Copy 的"批量创建"；RB/Data-and-Dependencies 的"生成器的实际产物" | 符合（RB 已有规定，原型尚未实现） |
| 7.5.3 | `samples/DefaultGame/Main.cs:31` 按场景把全局 `MaxResourceStates` 调到 65,536 | 完成 1.3 和 5.5 之后删除这个覆盖值，使用默认值 | #444 | 符合 |

### 7.6 存档

| 编号 | 当前 | 修改 | 依据 | 一致性 |
|---|---|---|---|---|
| 7.6.1 | 物理没有任何序列化代码 | 物理持有的数据（接触缓存、求解器状态、休眠状态、设备刚体状态）作为一个整块写入 ECS 存档：保存时回读设备数据，读档时上传；事件日志的读取进度按 4.2.5 处理 | ADR 0004、0005；RB/Serialization | 新增（PW 加一页 Physics-Save） |

### 7.7 物理验收

| 编号 | 内容 | 依据 |
|---|---|---|
| 7.7.1 | 接触的公开行为：两体共同平移 0.1 m 后保留接触和驻留力；料仓 24 粒完整 301 步；三形状泄流；事件 Begin/End 的顺序 | #443 的评论；PW/Contacts |
| 7.7.2 | MillionPhysicsActivity 原六帧、pyramids 前 3 步、silo 64 步：分别报告命令回放、成员日志、结构提交和整步时间，用 Release 配置 | #443 |
| 7.7.3 | 角色 2k/5k/10k：场景建立和整步的 mean/P95/max；阶段计时单独记录 | #440 |
| 7.7.4 | #445 的应用命令退出码为 0 并输出图像 | #445 |

## 8. 总验收与合入顺序

1. 阶段 0 → 阶段 1 → 阶段 2/3（同一分支）→ 阶段 4 → 阶段 5 → 合入 #447。
2. #442 改为基于 main 后完成阶段 6，再合入。
3. 门槛：各阶段的验收项全部通过；DefaultGame 三场景按 AG Rendering 不回退；880 行矩阵按 P1 处理。
4. 合入后清理本任务创建的工作树和产物（AG 的 Agent 工作区规则）。

## 9. 需要修订的文档

| 文档 | 修订 | 原因 |
|---|---|---|
| S407 §2 第 3 条、故事 14 | "接触实体保存接触特征与历史"改为"接触历史归物理接触存储，按形状对持久化" | D1 |
| PW/Contacts、PW/Events、PW/Graph-Solving | 按 7.1–7.4 重写 | D1、D2 |
| EW/Change-Tracking | 新增成员日志一节 | 4.3.2 |
| EW/Spatial-Maintenance、`wiki/architecture/animation/Timeline.md` | 删除 `EntityTableChanges`/`ObserveEntityTables` 的描述，改为成员日志 | 4.3.2 |
| 5.1 列出的 12 个 EW 页面 | `QueryCursor`/`QueryRow`/`QueryChunkView` 改为新签名 | 5.1 |
| RB/Data-and-Dependencies | Relationships 一行加入配对查询 | 5.2 |
| RB/Acceptance | 门槛按 P1 修订 | P1 |
| ADR 0005 | 版本 4 → 5 | 6.1 |
| 新 ADR | 统一结果交付机制：拉取式、存储由生产者持有、基于 Job 资源范围、无回调；注明与 ADR 0003 的关系（只从原生事实派生，不替代） | D2、D3 |
| #443、#444、#445、#446、#447 | 按本计划更新范围；#443 缩小为批量结构操作与成员日志语义 | — |
