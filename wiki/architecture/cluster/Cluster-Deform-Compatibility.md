# Cluster Deform Compatibility

## Definition
一个 cluster 只能对应一个 Deform compatibility，但同一个 Mesh 可以包含使用不同 Deform implementation 的不同 clusters。

## Cook rule
triangles 在 meshlet/cluster 建立前按 Deform compatibility 分区；同一分区内仍可包含多个 Material ranges，只要它们共享 Deform compatibility。

## Benefits
- visible cluster → one Deform work
- one opaque cache region
- raster 不需要按 material range 再选择 Deform cache
- shared vertex 不被不同 Deform 产生互相矛盾的位置

## Relations
[[architecture/material/Deform-Compatibility]] · [[Deform-Cache]]