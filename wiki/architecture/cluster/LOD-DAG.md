# LOD DAG

Cluster 的 LOD 关系保持 DAG。简化生成的父级 cluster 与基础 cluster 一起属于完整 cluster 集合。

LOD 关系不替代 spatial hierarchy；它只表达几何细节层级与 selection 所需关系。

导入器以 meshoptimizer 创建、分组及简化 cluster。简化输入同时包含原位置和
`RawAttribute` 属性，保留原顶点及属性索引；normal、tangent、color、UV 和用户属性
共同参与边折叠选择及误差。权重沿用固定 UE `Cluster.cpp:973–1020` 的定义，
从 UE 三角尺寸单位换算到 meshopt 当前组的包围盒单位。输出误差为原网格坐标绝对单位，渲染阶段按实例变换换算；
父层误差取本次简化误差与子层误差的最大值，供所有视图的同一 LOD 选择使用。

材质 shader 通过编译常量 `CLUSTER_RASTER_TWO_SIDED` 选择单面或双面；默认单面。
`CLUSTER_TARGET_RASTER_TWO_SIDED` 默认沿用材质选择，可由用户 shader 为目标深度视图
单独声明。共享入口将两项选择写入 `RasterFaces` 属性，HW 在建立 PSO 时消费该属性，
SW 使用同一编译常量形成覆盖及重心插值。glTF 导入从原材质 `DoubleSided` 生成对应变体。

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
