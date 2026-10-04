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
| 蒙皮绑定 | 逆绑定矩阵、关节到骨架的映射 | 中立 |
| 形变目标集合 | 目标名称、默认权重 | 中立 |
| `VertexStreamsBlock` | 每个图元的材质、三角形索引、全部顶点流、形变目标与诊断，见 [[architecture/Assets/Gltf-Mesh-Import|glTF Mesh Import]] | 中立，仅导入进程内 |
| 几何 | 顶点身份、位置、拓扑、LOD（Cluster：cluster 与 LOD DAG；普通管线：索引缓冲与离散 LOD） | 管线表示 |
| 顶点属性 | 法线、切线、全部 UV 与颜色、自定义属性，按该管线 Vertex 实现的编码 | 管线表示 |
| 蒙皮影响 | 每个顶点的全部关节与权重，按该管线的顶点顺序 | 管线表示 |
| 形变增量 | 逐目标、逐顶点增量，按该管线的顶点顺序 | 管线表示 |

当前代码与目标的差异：Cluster 管线只有一个 `ClusterMeshBlock`，位置与属性在同一段编码中；普通管线没有表示 Block；没有蒙皮、形变的 Block。拆分 Cluster 的几何与属性会改变渲染侧读取 cluster 数据的方式。

## Relations
[[architecture/Assets/Asset-Meshes|Mesh]] · [[architecture/Assets/Gltf-Mesh-Import|glTF Mesh Import]] · [[architecture/Assets/Importer-Sets|Importer Sets]] · ADR 0016
