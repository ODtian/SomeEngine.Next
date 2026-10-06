# LOD DAG

Cluster 的 LOD 关系保持 DAG。简化生成的父级 cluster 与基础 cluster 一起属于完整 cluster 集合。

LOD 关系不替代 spatial hierarchy；它只表达几何细节层级与 selection 所需关系。

参见 [[Spatial-BVH]]。

Cluster 几何元数据保存该 cluster 的最大源三角形边长，按向上取整的 half 编码在
`PackedGroup` 高 16 位；低 16 位继续保存简化组编号。导入器从源三角形计算一次该值，
96 字节 cluster 布局与 opaque vertex blob 的解释职责继续保持。

阴影视图使用自身投影的球深度/视角标度，将最大边长转换为虚拟像素，再按该视图的
硬件边阈值选择 HW/SW。SW 的 64 像素覆盖范围由实际当前边界限定。
节点和 cluster 的几何误差采用一致的实例最小伸缩值，球半径及边长采用最大伸缩值。
固定原版依据为 `NaniteClusterCulling.usf:255–308` 与 `NaniteShared.cpp:195–202`，
提交 `396c9f059903aed5fec78ecd3d437a40c6415368`。最大源边长的公开导入行为见
`ClusterTriangleEdgeTests.ThreeFourFiveTriangleCarriesItsLongestSourceEdge`。
