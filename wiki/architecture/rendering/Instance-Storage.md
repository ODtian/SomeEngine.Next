# Instance Storage

## Ownership and frame lifetime

RenderWorld 保存来源组件；composition/producer 将这些组件组合为批行。`RenderInstanceStorageSystem` 持有 `RenderInstanceResources`，后者提供双代持久映射的属性与批元数据存储。批行和 generation 都不是 Entity 身份，不能写回 ECS 作为实体标识。

`RenderFrameCoordinator` 管理 prepare、prepared/committed frame lease 与 GPU 完成 fences。写入通过 prepare scope 选择可写 generation；消费帧登记实际使用的 generation，所选代仍被未完成帧使用时不能覆写。`RenderInstanceBatchView` 的属性范围、元数据范围及边界属于该帧使用期；lease 结束后不能继续访问借来的映射/视图。在有效 lease 内复制到自身数组的元数据快照可以独立保留。

每张 RenderGraph 通过 storage 的 `GetDataTransfer(graph)` 取得 transport；图内消费者读取 transport 暴露的持久 GPU 资源。图资源状态与已提交 GPU 工作由 RenderGraph 管理，CPU prepare 权限、存储 generation 和帧使用期由 coordinator/storage 管理；不恢复旧 `Install(graph)`/`RenderDataLock` 所有权。

## Publishing batch updates

批更新在所选映射代中写入属性与元数据，实际有写入的发布推进 `ContentRevision`；该版本表示批内容变化，不是物理 transport generation。各 producer 的 changed-row 结果由 composition 汇集。可选列未写入时保留既有绑定和值；已绑定的可选逐实例列没有变化行时不发布新的内容版本；完全没有绑定任何授权属性的更新仍是无效操作。完整替换的列必须写完要求的行。取消或失败沿 storage 的 rollback/lease 路径恢复，不得提前把未发布内容当成已接受帧数据。

核心 producer 持有 transform 字段；Cluster producer 持有 geometry、deform、xray、depth 与 first-person 等 Cluster 字段；材质 producer 持有 binding 与 blend 字段。共享 MaterialId/实际引用仍由 RenderWorld 提取路径维护，不新增逐实体材质所有者或独立 first-person 镜像系统。

DataTransfer 根据发布的 `ContentRevision` 将批对应的 `PropertyDataRanges` 复制到图的持久属性缓冲；元数据仍从所选 generation 的映射缓冲按帧使用期取得。只有复制 pass 的实际提交确认才推进 transport 已接受版本，部分失败时仍保留已接受的资源边界。CPU 端的 changed-row/可选列写入语义不等于 GPU 只复制这些属性的字节：当前 transport 仍按需要更新的批范围复制，精确逐属性 GPU byte-copy 粒度尚未闭环。不能据此宣称每字节仅一次写入/复制或稳定帧加速已经恢复。

## Query and geometry inputs

`IRenderInstanceBatchSource<TGroupKey>` 保留 `EntityQuery` 与 `Current` 的公开契约，不要求所有实现提供 `EntityQueryHandle`。具体 composition 可以持有自己的 handle，但由 composition 创建、释放并保证查询与 RenderWorld 的生命周期；消费者不得据此取得额外查询所有权。

Cluster 的实际几何来源为 `RenderClusterGeometry`，通用 GPU geometry 路径不要求 `RenderMesh`。空间分组沿同一查询与行映射关联实体位置和批行；行地址只对当前批计划有效。网格、LOD 与 shadow caster 的专用缓存仍由各消费者负责，不将这些字段塞回通用 composition。

## Verification boundary

公共验证应检查单行变化、批成员替换、取消/失败更新、动态连续帧、放弃帧后的重新准备、多组局部更新、未写列保留绑定、空更新、不完整替换，以及 CPU prepare 与 GPU 在途帧不互相覆写。值与回读正确不等于 GPU 复制字节粒度已验证。

旧测试或旧架构下的运行记录不代表本次移植已通过。依赖项目编译与生成器探针不能替代 Render/Cluster 编译、这些公共行为测试或 GPU 执行。

## Relations

[[GameWorld-and-RenderWorld]] · [[Extraction]] · [[architecture/render-graph/Frame-Uploads]] · [[architecture/render-graph/Readback]]
