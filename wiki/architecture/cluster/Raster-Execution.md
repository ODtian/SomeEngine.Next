# Raster Execution

## Definition
Indirect draw/dispatch 只是 Cluster triangle raster pipeline 当前内部命令机制，不是 Cluster 对外业务语义。

## Invariants
- Cluster 产生 bounded work sets 并按 stage compatibility grouping。
- Cluster 自己拥有 SW/HW raster command recording；调用方不安装 raster command callback，也不选择如何发 draw/dispatch。
- 当前内部可以使用 indirect draw/dispatch；将来具体 pipeline/backend 可以改用 mesh shader、work graph 或其它机制，而不改变 Cluster 对外 geometry/material 语义。
- 不公开只为替换底层命令方式而存在的 raster executor 对象。

## Relations
[[Hardware-Raster]] · [[Software-Raster]] · [[architecture/material/Raster-Compatibility]]