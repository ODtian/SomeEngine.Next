# Instance Storage

## Data and buffers

RenderWorld 保存实例属性的来源组件。`RenderInstanceStorageSystem` 为一张 RenderGraph 安装
属性数据和批元数据两份持久 device-local 缓冲；图持有 GPU 缓冲和上传页的生命周期。
批保存行范围、属性布局和供场景 Feature 使用的内容修订。读取视图交付图缓冲身份与批元数据范围。

## Writing a frame

帧循环通过 `TryOpenNextFrame` 取得下一帧，随后把它传给 `RenderPrepareSystems.Update(scope, next)`。
批构建按变化的属性和行，从下一帧上传页取得目标写入跨度。连续 ECS 组件跨度直接复制到该跨度，
带填充的编码分别写入值和填充字节。元数据按属性的字范围写入。

每个批更新持有一组 `RenderGraphBufferWrites`。发布批时提交范围，取消批更新时释放这组记录。
逐实例绑定在发布时只提交实际写过的列；可选列没有变化行时保留原绑定和值，整批没有变化时
不提交上传、不推进内容修订。声明为完整替换的列仍必须绑定并写完所有行。
实体表变更通过同一实例查询筛选当前成员，数量变化由本次查询的行数检测。
可选实例组件的 archetype 迁移触发成员重建；场外贴花、灯光等其它实体的表变更保留已有实例批。
材质序列偏移直接在映射跨度中解析，不经过 CPU 临时数组。
同帧其它批的写入继续生效。上传复制先于图中的实例读者，范围外数据保留在持久缓冲中。
缓冲首次使用时，清零范围取本帧上传范围的补集，直接在图上传页中填零，与实例值一同复制；每个字节完成一次 CPU 写入和一次 GPU 复制。
图根据自己的跨帧缓冲状态安排后续访问。

图提交回调确认本次实例上传后，下一轮准备沿系统变化版本写入变化行。下一帧被放弃或上传中断时，
下一轮准备从 RenderWorld 重新写入实例属性，恢复 GPU 与组件的一致性。

## Spatial grouping

实例来源交付组合使用的 `EntityQuery`。Cluster 场景排序通过同一查询读取 `RenderTransform.Position`，
用 `RowsForEntity` 把实体位置填入批行位置，再调用 Meshopt 空间排序。批成员变化时更新排序，
移动中的批沿用组成员并由 GPU 重拟合包围盒。

## Behavior evidence

`InstancePreparationTests` 使用 WARP、自给的 ECS 组件和公共持久回读，验证单行变化、批成员替换、
取消批更新、连续动态帧和放弃下一帧后的重新写入。`InstanceSparseUpdateTests` 通过同一公开
读回验证多组局部更新、未写列保留共享绑定、空更新与不完整替换。`ClusterSpatialSortingTests` 使用完整 SM 6.6
Cluster 着色组合和硬件适配器，读取多个 ECS archetype 的空间分组渲染结果。

## Relations

[[GameWorld-and-RenderWorld]] · [[Extraction]] · [[architecture/render-graph/Frame-Uploads]] · [[architecture/render-graph/Readback]]
