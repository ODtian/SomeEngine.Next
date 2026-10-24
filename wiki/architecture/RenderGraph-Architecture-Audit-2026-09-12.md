# RenderGraph 持久结构原则复核

工作树：`F:/SomeEngine.Next-render-enhancement`。基线提交：`4b87345c`，包含当前未提交修改。审查对象是 RenderGraph 核心及内置 Cluster、材质、纹理调用链，不代表对整个引擎所有自定义调用方的穷尽证明。

## 判断标准

用户随后补充：不适合逐项 CRUD 的情况可以采用符合常识、范围明确且成本合理的局部 reconcile；如果不是当前无法达到 1 ms 的主因，不应为追求形式上的纯 CRUD 而优先大改。所以下列 7 类是需要评估的设计与成本清单，不意味着全部必须立即重写。持久结构仍为主体，合理局部比较和变化后的局部重组可以保留。

图声明与普通属性持久存在；显式 CRUD 或属性修改入口登记真实变化；派生依赖、活跃性、调度、同步和资源关系只更新受影响区域。执行阶段消费这些结果，不以重新声明和逐帧匹配作为正常结构维护方式。允许一次性上传等即时工作，但应作为局部扩展接入。命令录制、提交、完成信号、交换链和真实设备状态处理不属于“发现图有没有变化”。

当前确认 **7 类独立偏离**。同一链条有多个落点，以下按需要单独修正的数据机制计数，不按循环、函数或文件数量重复统计。扩展点的隔离能力另列为待专项证明，不混入 7 类数量。

## 确认的偏离

| 编号 | 问题与证据 | 影响及应有归属 |
|---|---|---|
| 1 | 常驻纹理每帧重新导入并创建收尾 pass。`PrepareTexture`/`PrepareMissingTexture` 即使没有新上传，也进入 `EnsureImported`、`EnsureFinalizationPass`；后者用 `frame.AddGeneralPass` 创建读取终态的空回调节点，并逐帧连接消费者顺序。见 [RenderDevice.Textures.cs:106](/F:/SomeEngine.Next-render-enhancement/src/SomeEngine.Render/RenderDevice.Textures.cs:106)、[397](/F:/SomeEngine.Next-render-enhancement/src/SomeEngine.Render/RenderDevice.Textures.cs:397)、[411](/F:/SomeEngine.Next-render-enhancement/src/SomeEngine.Render/RenderDevice.Textures.cs:411)。 | 真实纹理长期存在，图中的资源/收尾关系却具有帧寿命。关系应由纹理注册、消费者关系及实际上传变化维护；不能仅因进入下一帧就重建。一次性 mip 上传本身不算违规。 |
| 2 | 常驻材质资源绑定仍逐帧遍历、解析和重放。Cluster 的 `UpdateMaterialInputs` 枚举全部已安装材质输入，调用资源获取和 `SetParameterInput`；另一套 `MaterialBindless.Use → UpdateMaterial` 也逐次遍历绑定并比较属性版本，`EnsureFrame` 重建值缓冲的导入和 SRV。见 [ClusterPipelineSystem.Materials.cs:1235](/F:/SomeEngine.Next-render-enhancement/src/SomeEngine.Render.Cluster.Pipeline/ClusterPipelineSystem.Materials.cs:1235)、[MaterialBindless.cs:189](/F:/SomeEngine.Next-render-enhancement/src/SomeEngine.Render.MaterialBindless/MaterialBindless.cs:189)、[693](/F:/SomeEngine.Next-render-enhancement/src/SomeEngine.Render.MaterialBindless/MaterialBindless.cs:693)。 | 材质属性已有修改事实，却仍由使用过程发现是否变化。材质—资源—消费者关系应持久维护，属性和驻留变化通知受影响输入。这里没有把必要的 shader 执行和变形计算算作违规。 |
| 3 | 用于常驻绑定的三类图输入采用逐帧必填语义。`ClearFrameData` 清空 buffer、texture、parameter 输入；对应 setter 重新标脏，执行时通过 `UpdateInputRows` 等比较映射结果。见 [RenderGraphFrameState.cs:1328](/F:/SomeEngine.Next-render-enhancement/src/SomeEngine.RenderGraph/RenderGraphFrameState.cs:1328)、[1420](/F:/SomeEngine.Next-render-enhancement/src/SomeEngine.RenderGraph/RenderGraphFrameState.cs:1420)、[1445](/F:/SomeEngine.Next-render-enhancement/src/SomeEngine.RenderGraph/RenderGraphFrameState.cs:1445)、[1472](/F:/SomeEngine.Next-render-enhancement/src/SomeEngine.RenderGraph/RenderGraphFrameState.cs:1472)、[FrameInputRows.cs](/F:/SomeEngine.Next-render-enhancement/src/SomeEngine.RenderGraph/FrameInputRows.cs)。 | 同一持久资源值也无法自然保留，普通绑定必须逐帧重放。帧专用上传切片需要独立寿命；修正不能简单删除 `Clear` 并保留旧帧 ID，而应给常驻输入持久值及明确更新入口，局部即时输入另行接入。 |
| 4 | 驻留页访问关系每帧重新声明，再整体比较声明列表。`DeclareResidencyReads` 对全部页堆消费者调用 `ReadRanges`，范围集合未变化时仍如此；`PrepareBufferRangeReads` 每帧重建已解析列表并 `SequenceEqual`。见 [ClusterPipelineSystem.Resources.cs:777](/F:/SomeEngine.Next-render-enhancement/src/SomeEngine.Render.Cluster.Pipeline/ClusterPipelineSystem.Resources.cs:777)、[ClusterPipelineOutput.cs:180](/F:/SomeEngine.Next-render-enhancement/src/SomeEngine.Render.Cluster.Pipeline/ClusterPipelineOutput.cs:180)、[RenderGraphFrameState.cs:804](/F:/SomeEngine.Next-render-enhancement/src/SomeEngine.RenderGraph/RenderGraphFrameState.cs:804)、[FrameExecutor.cs:902](/F:/SomeEngine.Next-render-enhancement/src/SomeEngine.RenderGraph/FrameExecutor.cs:902)。 | 页驻留有明确变化来源，范围集合也已持久存在，但消费者关系在帧入口重放。应由驻留范围或消费者增删更新访问声明；比较上一帧列表不是正常更新接口。 |
| 5 | 动态资源、视图、pass、access 在每次 `BuildRows` 中全部重新接入，并通过 `Set*Row`/`Observe*` 比较上一轮描述。正常纹理、输入和读取关系也进入此路径。见 [FrameExecutor.cs:142](/F:/SomeEngine.Next-render-enhancement/src/SomeEngine.RenderGraph/FrameExecutor.cs:142)、[682](/F:/SomeEngine.Next-render-enhancement/src/SomeEngine.RenderGraph/FrameExecutor.cs:682)、[FrameAnalysis.cs:931](/F:/SomeEngine.Next-render-enhancement/src/SomeEngine.RenderGraph/FrameAnalysis.cs:931)、[982](/F:/SomeEngine.Next-render-enhancement/src/SomeEngine.RenderGraph/FrameAnalysis.cs:982)。 | 派生执行数据仍承担发现结构变化的职责。显式即时声明本身可以新增局部结构；问题是常驻关系被当作即时描述反复接入，再用比较维持缓存。持久部分的 `ChangedRows` 已按变更集合工作，不能将它误称为全量重建。 |
| 6 | 动态顺序按带帧 ID 的声明保存和失效。`ClearFrameData` 删除旧声明，新帧 `OrderAfter` 加入新声明；`EndRows` 只要 `_dynamicOrderChanges` 非空就令分析失效。见 [RenderGraphFrameState.cs:787](/F:/SomeEngine.Next-render-enhancement/src/SomeEngine.RenderGraph/RenderGraphFrameState.cs:787)、[FrameSchedule.cs:501](/F:/SomeEngine.Next-render-enhancement/src/SomeEngine.RenderGraph/FrameSchedule.cs:501)、[FrameExecutor.cs:664](/F:/SomeEngine.Next-render-enhancement/src/SomeEngine.RenderGraph/FrameExecutor.cs:664)。 | 相同的真实关系因描述身份轮换而反复维护。本次默认场景图帧 ID 为 100 的探针看到：进入行更新前分析未失效，动态顺序变更 6 项；行更新后分析失效，但实际变化的分析资源和 pass 均为 0。应持久维护正常关系，增删入口直接更新；把比较改成另一种边集合仍不能修正调用方的逐帧重放。 |
| 7 | 分页表局部写入仍复制整个页目录。`GraphTable.WritePage` 在目录共享时执行 `_pages.Clone()`；每页 64 行，因此一次局部编辑的这部分开销随表容量增长。见 [GraphTable.cs:57](/F:/SomeEngine.Next-render-enhancement/src/SomeEngine.RenderGraph/GraphTable.cs:57)。 | 行数据只复制受影响页，不能再描述为“复制整张行表”；但无关页的引用目录仍被复制，未满足端到端局部更新。这项来自本轮此前的未提交性能优化，需要修正，不能只把减少复制字节数当作架构合格。 |

## 不能误判为违规的现有机制

- `TryReuseAnalysis` 目前只是 `_analysisReady && !_analysisChanged` 的常数时间判断，本身没有全图匹配。真正的问题在前面的逐帧重放、列表构造和比较。名称或存在一个 dirty 判断不是架构违规的充分证据。
- 持久 CRUD、事务丢弃、反向索引、局部值依赖/活跃性/调度更新已经存在。`GraphStructureIndex` 消费 `GraphEditChanges`；`FrameMaterialization`、同步和参数连接的主要循环消费变更集合，不能全部列成每帧扫描全图。
- 每个在途帧各有临时资源、命令存储和实际边界状态是设备寿命要求，不是保存多套任意图版本。成功提交后的同步状态和完成信号更新必须保留。
- 局部资源改变后比较它的范围/内容，或者 setter 对相同值早退，可以是修改驱动更新的一部分。不能将所有 `SequenceEqual`、字典或缓存一律删除。
- 临时分配器在帧重置时遍历缓存资源检查 `IsDisposed`，属于生命周期通知仍不完整的观察点；这里暂不把设备生命周期检查计为第 8 类结构违规。是否能移到明确的释放通知需要另行核对引用与在途安全。

## 尚未证明的扩展边界

`RenderGraphFrameExtension` 目前主要提供插入位置，创建的资源与 pass 写入同一 `RenderGraphFrameState`，没有独立保存扩展区域的派生分析。共享底层表本身不构成违规，因为局部变更集合也可以实现隔离。必须通过“只修改一个扩展区域，旁侧无关区域规模增长”测量来证明无关区域没有被重新接入或分析。当前没有这一证明，不把旧报告的全部隔离批评直接当作今天已验证的新故障。[扩展入口](/F:/SomeEngine.Next-render-enhancement/src/SomeEngine.RenderGraph/RenderGraphFrameExtension.cs:34)

## 为什么此前没有闭合

Git 历史证明这些机制不是本次临时试验才出现：

- `03efeaf3`（2026-08-23）已有帧级动态集合与结束清理。
- `e323d961`（2026-09-08）已包含三类输入清空与范围读取的帧级接入；该提交的名称是保留工作树，不能据此推断具体由谁或在哪一次会话首次设计。
- `4b87345c`（2026-09-09）包含当前动态顺序的三个映射及按帧删除/添加的通知。
- 本轮之前加入的 `GraphTable`、常量上传等未提交优化进一步降低了局部成本，但没有把正常调用链改成完全由修改入口维护。

9 月 5 日审查已明确要求“修改入口记录真实变化，无变化时不构造比较输入”。后续确实移除了旧的全量分析输入缓存，建立了增量索引和传播，但普通调用方仍持续生成即时描述。将“保留了结果、减少了重算”当作“无变化时不发现变化”，造成了架构验收偏差。

现有公开行为测试主要验证输出、同步、增删、取消和失效资源；`FrameAnalysisReuseTests` 还验证重新声明之后能命中分析结果。这些可以证明功能或局部缓存行为，不能证明正常渲染帧没有重放结构。编辑统计的重算节点数也不能证明没有复制页目录。需要补充公开 API 驱动的规模实验：固定本次修改，增加无关结构；以及固定完全不变的正常调用链，测量图维护工作和分配，而不是只看复用布尔值。

## 本次状态与修正顺序

本次临时加入的“每帧解析动态边后比较集合”试验已全部撤回，未作为修复保留；其新增测试在编译阶段因参数类型错误未运行，随后一并删除。探针和临时程序、日志、追踪结果也已清理。此前验证通过的 GPU 区域工作分配及其他既有修改保留。本次没有新的性能提升结论。

最初拟先迁移正常调用链的持久纹理、材质输入和驻留访问关系。按用户后续补充，现已改为先量化成本，再选择影响 1 ms 差距的主要工作；整套输入接口迁移暂停。即时子图与在途资源边界仍须保持正确性。UE5 只能用于参考具体算法与任务组织，不能用其逐帧 RDG 构建方式替换用户明确要求的持久图架构。

后续详细追踪测得依赖分析约 0.0215 ms、调度约 0.00024 ms、生命周期/frontier 更新约 0.00071 ms；这些不是当前主要差距。反而提交批次在三个在途执行实例上重复出现 3/6/3 次：已有批次只在需要时拆分，依赖消失后没有合并。后续改动在变化影响范围内检查相邻批次，只有不增加原有等待、也不推迟已有消费者时合并。这属于本项目允许的局部 reconcile，相关验证与性能数据继续记录在 [帧时间记录](/F:/SomeEngine.Next-render-enhancement/wiki/architecture/Render-Frame-Time-2026-09-12.md)。
