# Page Heap

## Definition
Cluster page bytes 常驻于 device-local、shader-readable 且可作为 copy destination 的 Buffer。

网格注册时按页堆分配粒度计算每页的流式字节数，与页来源的注册索引一起保存。
最低细节页的字节单独计入常驻基础容量，其流式字节数为零。每帧需求预算直接读取这些
固定值，淘汰选择也通过同一份页数据识别可流送的细节页。

## Invariants
- CPU 不直接 map 并修改正在被 shader 读取的长期 PageHeap。
- async I/O 写 staging，RenderGraph copy 到尚未暴露的新 range。
- coarsest/root geometry pages 构成 Mesh 的 [[architecture/assets/Bootstrap-Streaming|Bootstrap]]，保证最低有效几何常驻。

## Relations
[[Page-Install]] · [[Resident-Ranges]]
