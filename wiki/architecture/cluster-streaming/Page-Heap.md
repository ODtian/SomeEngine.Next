# Page Heap

## Definition
Cluster page bytes 常驻于 device-local、shader-readable 且可作为 copy destination 的 Buffer。

## Invariants
- CPU 不直接 map 并修改正在被 shader 读取的长期 PageHeap。
- async I/O 写 staging，RenderGraph copy 到尚未暴露的新 range。
- coarsest/root geometry pages 构成 Mesh 的 [[architecture/assets/Bootstrap-Streaming|Bootstrap]]，保证最低有效几何常驻。

## Relations
[[Page-Install]] · [[Resident-Ranges]]