# Block Division

## Definition
按正交性划分 Block（ADR 0016）。一组数据满足以下条件时成为独立的 Block，由独立的 Importer 产出：

| 判断问题 | 例子 |
|---|---|
| 读它的运行时阶段不同吗？ | 位置被光栅、剔除、阴影读取；蒙皮影响只被 Deform 读取 |
| 它可以单独不存在、单独驻留或加载吗？ | 静态网格没有蒙皮；不可见的角色不上传蒙皮数据 |
| 替换它的理由与其它数据不同吗？ | 换 cluster 划分算法与换蒙皮编码互不相关 |

不拆的情况：必须一起决定才正确的数据（同一 Vertex 实现解码的法线、切线、UV）；拆开只多一层转发的数据（关节索引与权重）。

## Invariants
- Block 分两类，能否共用只看是否依赖某条管线的顶点身份或编码：
  - 中立 Block 不依赖，所有管线共用；
  - 管线表示 Block 依赖，每条管线各一份。
- 逐顶点的管线表示共享该管线的顶点身份。顶点身份由几何 Importer 决定，它在焊接时比较所有存在的顶点流（包括蒙皮与形变）。其它表示 Importer 通过 `GetBlockAsync` 显式取得映射。
- 只存在于导入进程内的中间数据也是 Block（例如准备后的顶点流），同一次导入中被多个 Importer 共用，保存时跳过。持久声明与资产分支规则见 [Asset Objects](Asset-Objects.md)。
- Block 缺失表示该特性不存在：Importer 为每个来源元素交付适用的 Block，读取方使用 [Context 的可选获取入口](Asset-Import-and-Storage.md) 与 [Asset 的可选获取入口](Asset-Objects.md) 处理缺失。
- 引擎不对来源数据设阈值：骨骼影响数量、UV 集、颜色集与自定义属性照搬来源，如何使用由着色器决定。
- 管线表示原则上不共用；某个读者显式读取另一条管线的表示时，它只是该 Block 的另一个读者。

## Current design
可选输出与导入进程内 Block 已由上述公共 API 实现。对应行为测试见 [ImportBlockBehaviorTests](../../../tests/SomeEngine.Assets.Tests/ImportBlockBehaviorTests.cs)。

网格的划分（以带蒙皮与形变目标的 glTF 网格为例）：

| Block | 内容 | 类别 |
|---|---|---|
| `MeshBlock` | 区段（材质槽）、包围盒、来源映射 | 中立 |
| `SkinBindingBlock`（`SkeletonAsset`） | 按来源关节顺序保存逆绑定矩阵、关节到骨架的映射 | 中立 |
| `MorphTargetSetBlock`（`MeshAsset`） | 按来源目标顺序保存名称、默认权重 | 中立 |
| `VertexStreamsBlock` | 每个图元的材质、三角形索引、全部顶点流、形变目标与诊断，见 [[architecture/Assets/Gltf-Mesh-Import|glTF Mesh Import]] | 中立，仅导入进程内 |
| 几何 | 顶点身份、位置、拓扑、LOD（Cluster：cluster 与 LOD DAG；普通管线：索引缓冲与离散 LOD） | 管线表示 |
| 顶点属性 | 法线、切线、全部 UV 与颜色、自定义属性，按该管线 Vertex 实现的编码 | 管线表示 |
| 蒙皮影响 | 每个顶点的全部关节与权重，按该管线的顶点顺序 | 管线表示 |
| 形变增量 | 逐目标、逐顶点增量，按该管线的顶点顺序 | 管线表示 |

`SomeEngine.Animation.Pose` 的 `SkinBindingBlock` 由 `SkinBindingImporter` 在每个 skin 的骨架资产上产出，通过 `context.GetBlockAsync<SkeletonBlock>` 消费所选骨架生产者的 `JointBones`。来源逆绑定矩阵保持关节顺序与数值，省略时每个关节使用单位矩阵。同一 mesh 被多个 skin 的节点复用时，节点的 `SkeletonSource` 指向各自的绑定与骨架；网格资产继续共用。

`SomeEngine.Assets.Schema.MorphTargetSetBlock` 由 `MorphTargetSetImporter` 在网格资产上产出。`Names` 来自 `mesh.extras.targetNames`，省略时为每个目标保留空名称；`DefaultWeights` 来自 `mesh.weights`，省略时为零。节点权重属于实例数据，网格 Block 保留 mesh 默认值。静态网格通过可选读取入口表达特性缺失。两个 Importer 随各自包的中立组合启用。

公开行为验收见 [DeformationBlockBehaviorTests](../../../tests/SomeEngine.Animation.Pose.Tests/DeformationBlockBehaviorTests.cs)：覆盖 `.gltf`、`.glb` 保存重开、共享 mesh 的不同 skin、来源关节重排、替换骨架生产者、省略值、静态网格与重新导入更新。

当前代码与目标的差异：Cluster 管线只有一个 `ClusterMeshBlock`，位置与属性在同一段编码中；普通管线没有表示 Block；管线蒙皮影响与形变增量 Block 待实现。拆分 Cluster 的几何与属性会改变渲染侧读取 cluster 数据的方式。

## Relations
[[architecture/Assets/Asset-Meshes|Mesh]] · [[architecture/Assets/Gltf-Mesh-Import|glTF Mesh Import]] · [[architecture/Assets/Importer-Sets|Importer Sets]] · ADR 0016
