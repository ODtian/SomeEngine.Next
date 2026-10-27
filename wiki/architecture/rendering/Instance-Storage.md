# Instance Storage

## Ownership and frame lifetime

RenderWorld 保存来源组件；composition/producer 将这些组件组合为批行。`RenderInstanceStorageSystem` 持有 `RenderInstanceResources`，后者提供双代持久映射的属性与批元数据存储。批行和 generation 都不是 Entity 身份，不能写回 ECS 作为实体标识。

`RenderFrameCoordinator` 管理 prepare、prepared/committed frame lease 与 GPU 完成 fences。写入通过 prepare scope 选择可写 generation；消费帧登记实际使用的 generation，所选代仍被未完成帧使用时不能覆写。`RenderInstanceBatchView` 的属性范围、元数据范围及边界属于该帧使用期；lease 结束后不能继续访问借来的映射/视图。在有效 lease 内复制到自身数组的元数据快照可以独立保留。

每张 RenderGraph 通过 storage 的 `GetDataTransfer(graph)` 取得 transport；图内消费者读取 transport 暴露的持久 GPU 资源。图资源状态与已提交 GPU 工作由 RenderGraph 管理，CPU prepare 权限、存储 generation 和帧使用期由 coordinator/storage 管理；不恢复旧 `Install(graph)`/`RenderDataLock` 所有权。

## Publishing batch updates

批更新在所选映射代中写入属性与元数据，实际有写入的发布推进 `ContentRevision`；该版本表示批内容变化，不是物理 transport generation。各 producer 的 changed-row 结果由 composition 汇集。可选列未写入时保留既有绑定和值；已绑定的可选逐实例列没有变化行时不发布新的内容版本；完全没有绑定任何授权属性的更新仍是无效操作。完整替换的列必须写完要求的行。取消或失败沿 storage 的 rollback/lease 路径恢复，不得提前把未发布内容当成已接受帧数据。

核心 producer 持有 transform 字段；Cluster producer 持有 geometry、deform、xray、depth 与 first-person 等 Cluster 字段；材质 producer 持有 binding 与 blend 字段。共享 MaterialId/实际引用仍由 RenderWorld 提取路径维护，不新增逐实体材质所有者或独立 first-person 镜像系统。

同代更新先保存批元数据，各列在绑定时保存所选共享值或逐实例值，取消沿原代恢复这些数据。跨代更新继续从已发布代补齐保留列。Cluster composition 收集的变化列同时决定绑定和 producer 写入范围。完整替换列依靠完整行数检查生成整列属性范围；可选列保留稀疏行掩码。源码入口：[实例写入与存储](../../../src/SomeEngine.Render/Instances/RenderInstanceResources.cs)、[Cluster 实例组合](../../../src/SomeEngine.Render.Cluster/ClusterSystems.cs)。

每个批只保留最近一次发布的不可变属性范围增量，包含前后 `ContentRevision`；`RenderInstanceBatchView` 连同所选 generation 一起捕获该增量。每张图独立维护 allocation 身份与已提交版本，只有复制 pass 的实际提交确认才推进该图的游标；暂存或丢弃帧不消费共享增量，部分失败时仍保留实际接受的资源边界。元数据仍直接从所选 generation 的映射缓冲按帧使用期取得。

当图的已提交版本恰好是该增量的前驱时，稳定逐实例绑定只复制实际写入的属性行范围；未变版本不复制。新图、allocation 复用或错过前驱版本仍回退到完整 `PropertyDataRanges`。共享值写入复制共享元素；转为逐实例绑定时复制完整的新可见列；自定义元数据保持完整批范围回退。因此这是常见相邻版本路径的稀疏复制，不是所有路径的最小复制承诺。

属性行掩码只在实际稀疏写入时惰性创建，以 `CompareExchange` 发布同一个数组，写入用原子位或合并；不增加互斥锁、全局登记表或多版本日志。首次写入竞争可能产生临时落选数组，另有规范化范围数组的 CPU 成本；尚不能据此宣称总分配更少、GPU 物理流量最小或稳定帧更快。

## Query and geometry inputs

`IRenderInstanceBatchSource<TGroupKey>` 保留 `EntityQuery` 与 `Current` 的公开契约，不要求所有实现提供 `EntityQueryHandle`。具体 composition 可以持有自己的 handle，但由 composition 创建、释放并保证查询与 RenderWorld 的生命周期；消费者不得据此取得额外查询所有权。

Cluster 的实际几何来源为 `RenderClusterGeometry`，通用 GPU geometry 路径不要求 `RenderMesh`。空间分组沿同一查询与行映射关联实体位置和批行；行地址只对当前批计划有效。网格、LOD 与 shadow caster 的专用缓存仍由各消费者负责，不将这些字段塞回通用 composition。

## Verification boundary

公共验证应检查单行变化、批成员替换、取消/失败更新、动态连续帧、放弃帧后的重新准备、多组局部更新、未写列保留绑定、空更新、不完整替换，以及 CPU prepare 与 GPU 在途帧不互相覆写。普通值回读只证明值正确；独立图游标、丢弃/跳版本回退、绑定切换及范围外哨兵保持不变的公共测试用于检查实际受影响的字节覆盖。哨兵检查仍不等于硬件流量计数、范围内无重复复制或性能测量。

旧测试或旧架构下的运行记录不代表本次移植已通过。依赖项目编译与生成器探针不能替代 Render/Cluster 编译、这些公共行为测试或 GPU 执行。

## Relations

[[GameWorld-and-RenderWorld]] · [[Extraction]] · [[architecture/render-graph/Frame-Uploads]] · [[architecture/render-graph/Readback]]
