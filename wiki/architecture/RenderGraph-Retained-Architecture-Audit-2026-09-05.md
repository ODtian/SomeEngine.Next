# RenderGraph 持久优先设计核查

日期：2026-09-05。对象：`F:/SomeEngine.Next-render-enhancement` 当前工作区，包括已有未提交修改。本次仅核查和外部测量，没有修改产品代码。

## 用户明确的设计基准

- RenderGraph 以持久结构为主体，类似 retained UI；结构通过 CRUD 修改。
- 结构变化只 reconcile 受影响区域，保留无关局部结构和结果。
- 非结构变化属于属性更新，不因此重建结构；有依赖的派生结果按影响范围更新。
- 允许局部逃生舱插入即时子图。
- 可以有局部缓存，但不能以保存多个编译图 variant、逐帧匹配命中作为整体架构。
- 一帧真正没有变化时，不应为确认“没有变化”而扫描结构、构造比较输入或进行图匹配。必要的命令执行、提交、完成信号和设备状态处理另计。

## 结论

当前有真实的持久存储、变更集合及局部依赖更新，不能描述为“完全每帧重建的即时图”。但 CRUD 外围仍有与全图规模相关的复制和扫描，帧执行又重建统一的行集合并比较上一轮输入，尚未实现端到端的持久优先。

尤其需要区分：**保留无关结果，并不等于不访问或复制无关数据。** 当前局部更新统计只覆盖部分重算，不能代表总成本。

没有发现 `FrameAnalysisReuse` 保存多个编译图 variant；它保存每个执行器上一轮的输入和分析结果。不同在途帧的执行状态也不应称为不同图 variant。但逐帧构造全量输入并比对的机制，同样不满足上述无变化路径要求。

## CRUD 路径

`BeginEdit → GraphStructure.Fork → CRUD/GraphEditChanges → CommitEdit → GraphStructureIndex.Update → ApplyStructure → StructureVersion++`。

1. **真实局部性存在。** `GraphEditChanges` 记录访问、资源、pass、连接的修改；`GraphStructureIndex.Update` 只针对 `AffectedResources` 重建对应依赖时间线，维护变化边，并修补调度区间。未受影响资源的依赖对象和访问数组可保持同一引用。见 [GraphEditChanges.cs](../../src/SomeEngine.RenderGraph/GraphEditChanges.cs)、[GraphStructureIndex.cs](../../src/SomeEngine.RenderGraph/GraphStructureIndex.cs) 的 165、207、232 行。

2. **写时复制以整类存储为粒度。** `SlotMap<T>.EnsureWritable` 复制全部索引及全部行，对有 clone 函数的行逐个复制。改一个 pass 标签会复制全部 pass 对象及其参数/访问等集合；无关 buffer 类存储仍共享。它实现的是十类存储块之间的共享，并非任意局部节点的共享。见 [GraphStorage.cs](../../src/SomeEngine.RenderGraph/GraphStorage.cs) 的 161、482 行。

3. **局部更新外围仍是全量处理。** `GraphStructureIndex.Update` 无条件复制资源访问、pass 访问、视图关联、显式依赖、资源依赖时间线、依赖边计数、生命周期和分配记录等字典。`Clone` 是 `new(source)`，数组值可共享，但字典条目仍全量复制。它还遍历旧 pass 集合、全部临时资源，并重建调度位置字典。见同文件 125、160、201、225、248、266、889 行。

4. **局部调度修补前后仍扫描全图。** `RepairTopologicalSchedule` 构造全部 pass 的集合与列表，建立全部位置，遍历所有后继边查找逆序，再对局部窗口排序，最后全图验证。即使局部窗口为空仍验证；`Update` 调用该函数两次。`ScheduleNodesRevisited` 只记录最后窗口大小，不记录外围遍历，且第一轮修补的统计被丢弃。见同文件 671–782 行。

5. **提交的资源处理不消费局部修改集合。** `CarryBoundaryStates` 遍历资源表；`ApplyStructure` 遍历下一个结构的资源/视图；`RetireRemoved` 建立资源集合并遍历新旧资源。现有逻辑不会凭空重建所有 GPU 资源，但仍付出全量发现成本。见 [RenderGraph.cs](../../src/SomeEngine.RenderGraph/RenderGraph.cs) 的 344、430 行及 [ResourceAllocators.cs](../../src/SomeEngine.RenderGraph/ResourceAllocators.cs) 的 68、261 行。

6. **标签修改、相同值 setter 和空提交没有不同的失效路径。** `SetPassLabel` 调用 `GetMutable` 和 `TouchPass`；`CommitEdit` 无条件更新索引并递增 `StructureVersion`，空提交也一样。执行复用使用该全图版本，因此标签变化也会使后续分析复用失效。见 [RenderGraphEdit.cs](../../src/SomeEngine.RenderGraph/RenderGraphEdit.cs) 的 317、520 行、[RenderGraph.cs](../../src/SomeEngine.RenderGraph/RenderGraph.cs) 的 344–366 行。

## 帧执行、属性与即时子图

- **逐帧属性并非完整的持久属性体系。** `SetPassData` 写入帧数据；外部绑定、启用覆盖、范围覆盖等存进本帧集合。结束时 `ClearFrameData` 清除这些数据和覆盖；未继续提供的值不自然保留为上帧属性。即时数据需要清除，但不能据此把所有正常属性都设计成每帧重放。见 [RenderGraphFrameState.cs](../../src/SomeEngine.RenderGraph/RenderGraphFrameState.cs) 的 142、158、218、1341 行。

- **无变化的一般帧仍做全量匹配。** `FrameExecutor.BuildRows` 重新填充持久与动态资源/pass/access 集合；`TryReuseAnalysis` 遍历缓冲区、纹理、pass、access、边界内容和动态顺序，构造列表，再 `SequenceEqual` 比较上一轮输入。命中后还遍历回填队列、使用区间等结果。失配则整帧重新执行值、活性、依赖、调度及生命周期分析。见 [FrameExecutor.cs](../../src/SomeEngine.RenderGraph/FrameExecutor.cs) 的 161、194 行、[FrameAnalysisReuse.cs](../../src/SomeEngine.RenderGraph/FrameAnalysisReuse.cs) 的 15–134 行。

- **结构更新结果未成为帧执行的完整持久基础。** 结构索引确实提供 ID、pass/access 邻接等数据；但帧执行仍另行维护并推导自己的值依赖、活性、同步及队列调度。结构更新阶段的 `TopologicalSchedule` 和 `HazardTimelines` 没有直接作为帧执行的对应结果消费。两者语义不完全相同，不能直接替换，但局部修改收益在此处没有贯通。

- **特殊静态路径也不是零图检查。** `IsStableExecutionEligible` 每次执行被调用；满足前置条件时仍遍历所有持久 buffer/texture 检查资格。外部绑定、交换链图像、即时内容等会令该路径失效。更早的临时资源分配器 `Reset` 会枚举和复制分配 token，并重新检查/建立分配记录，没有结构未变就跳过的分支。见 [FrameExecutor.cs](../../src/SomeEngine.RenderGraph/FrameExecutor.cs) 的 82、124 行、[FrameSlot.cs](../../src/SomeEngine.RenderGraph/FrameSlot.cs) 的 24 行、[ResourceAllocators.cs](../../src/SomeEngine.RenderGraph/ResourceAllocators.cs) 的 560–613 行。因此不能把“纯静态路径跳过部分分析”说成符合无变化无检查要求。

- **即时扩展点提供插入位置，没有形成分析隔离边界。** `RenderGraphFrameExtension` 将资源和 pass 写入同一个帧状态；`BuildRows` 合并所有持久与动态 pass 后排序，统一构建访问集合；动态内容参与全量输入比较。扩展点内部结构变化会导致整体分析失配，不限于扩展区域和依赖边界。见 [RenderGraphFrameExtension.cs](../../src/SomeEngine.RenderGraph/RenderGraphFrameExtension.cs) 的 34 行、[FrameExecutor.cs](../../src/SomeEngine.RenderGraph/FrameExecutor.cs) 的 400–453 行。

- **调用方也存在无变化轮询。** Cluster 的 `ReconcileGraph` 有提前返回，并非每帧重装图；但返回条件调用 raster/shade 的 `IsCurrent`，两者都逐个比较 material revision。因此这条判断仍随材质数增长。见 [ClusterPipelineSystem.cs](../../src/SomeEngine.Render.Cluster.Pipeline/ClusterPipelineSystem.cs) 的 394 行和 [ClusterMaterialStages.cs](../../src/SomeEngine.Render.Cluster.Pipeline/ClusterMaterialStages.cs) 的 948、1298 行。未对其它 Feature 的所有更新路径作同等完整审计。

## 独立测量

测量工程和原始结果：`F:/.codex/research/render-retained-audit-20260905/`，`Audit.cs`、`Audit.csproj`、`results-final.json`、`summary.json`。直接引用现有 Release 产品程序集，无产品源码插桩；最终控制轮设置 `DOTNET_TieredCompilation=0`，避免分层编译切换混入规模对比。

构造相互独立的分支，每支含一个 64 字节临时 buffer、一个写 pass、一个读 pass。四种操作：交替改一个标签、反复设置完全相同标签、不调用 setter 的空提交、在两个固定 pass 间交替增删一条显式顺序边。只计 `BeginEdit + 修改 + Commit`，没有帧执行和 GPU 工作；初始化及引用检查在计时外。每组 12 次预热、36 次采样、3 轮交替图规模顺序，表中为三轮中位数的中位数。全部图使用同一个设备。

| pass 数 | 单标签修改 | 增删一条顺序边 | 空提交 | 单标签修改分配量 |
|---:|---:|---:|---:|---:|
| 64 | 60.4 μs | 42.3 μs | 31.6 μs | 96,472 B |
| 256 | 238.2 μs | 157.1 μs | 129.8 μs | 354,920 B |
| 1024 | 907.0 μs | 598.9 μs | 503.2 μs | 1,363,688 B |
| 2048 | 1883.9 μs | 1153.9 μs | 974.3 μs | 2,786,752 B |

- 重复设置相同标签：64 pass 为 60.3 μs，2048 pass 为 1754.3 μs；分配量与真正修改标签相同。
- 所有被测提交均使结构版本加 1，包括空提交。
- 标签 setter 后，远端无关 pass 对象引用发生变化；该远端资源的依赖时间线对象引用保持不变。
- 2048 pass 空提交分配 1,259,304 B，虽没有 setter，也存在明显全图成本。
- 初轮最小图仍有预热影响（标签中位数三轮为 197.2、59.8、60.4 μs）；原始数据保留，不把单次时间当作精密极限。默认分层编译的探索轮也保留为 `results.json`，另一次控制轮在 `results-no-tiering.json`。
- 该测量证明单点修改和空提交会随无关图规模增长；它不是先前真实场景的每帧耗时，也没有声称渲染器每帧执行空提交。

## 现有测试覆盖边界

[RetainedGraphIncrementalTests.cs](../../tests/SomeEngine.RenderGraph.Tests/RetainedGraphIncrementalTests.cs) 验证未受影响的存储类别、依赖对象、ID 和分配记录保留，检查局部重算计数。这是真实有效的保证，但没有约束字典复制量、整类行复制量、外围扫描量或无关图规模增长成本。该测试类由 `#if DEBUG` 包围，不在之前 Release 57 项测试中执行。

本次外部测量工程编译通过（0 警告、0 错误），最终 48 组测量完成。没有重新运行产品测试，也没有新增产品测试；本次没有实施修复。首次测量启动因外部输出目录缺少 D3D12 Agility SDK 失败，补齐该目录依赖后正常完成，此失败不作为图行为证据。

## 对后续实现的约束

修正方向应是：修改入口记录真实变化，持久数据保留未变化结果，局部更新沿实际依赖传播，即时子图通过明确边界接入。无变化时不为图更新而枚举节点、重建比较输入或挑选图版本。多帧在途的设备安全和跨帧状态仍必须正确维护，但不能成为逐帧全图发现变化的理由。

不应以增加编译图 variant、扩大全图输入缓存或放宽静态缓存命中条件代替上述要求。局部缓存可以用于降低局部计算成本；持久结构和修改驱动更新必须是正常执行路径。
