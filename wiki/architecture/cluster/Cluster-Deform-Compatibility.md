# Cluster Deform Compatibility

## Definition
一个 cluster 只能对应一个 Deform compatibility，但同一个 Mesh 可以包含使用不同 Deform implementation 的不同 clusters。

## Cook rule
triangles 在 meshlet/cluster 建立前按 Deform compatibility 分区；同一分区内仍可包含多个 Material ranges，只要它们共享 Deform compatibility。

`ClusterGeometryImporter` 在新几何表示中按 `ClusterDeform` 的静态、蒙皮、形变、蒙皮加形变四类拆分三角形，再分别建立 cluster 与 LOD DAG。类别来自准备流中所有 `JOINTS_n`/`WEIGHTS_n` 的存在和形变目标数量。各分区具有独立的 LOD 组编号；每个三角形保留来源材质编号。共享顶点身份在焊接时同时比较全部基础与形变流，保留蒙皮和形变造成的顶点差异。

扩展阶段的类别和来源映射供后续 Deform Importer 消费。影响与增量发生变化的边受到简化锁保护；数据恒定区域使用现有位置误差简化。

## Benefits
- visible cluster → one Deform work
- one opaque cache region
- raster 不需要按 material range 再选择 Deform cache
- shared vertex 不被不同 Deform 产生互相矛盾的位置

## Relations
[[architecture/material/Deform-Compatibility]] · [[Deform-Cache]]
