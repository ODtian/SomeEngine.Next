# Spatial BVH

Spatial BVH 将所有 LOD clusters 都作为 leaves，再在这些 leaves 之上建立空间层级。

内部节点只负责空间 culling；leaf 对应具体 cluster candidate。LOD DAG 与 spatial BVH 是正交结构。

参见 [[LOD-DAG]] · [[architecture/shader/Conservative-Bounds]]。