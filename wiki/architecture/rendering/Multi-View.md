# Multi-View

## Definition
View array 是所有 geometry pipeline 的一等输入。

## Invariants
- traversal、LOD/cull、deform、raster、shade 与 tessellation 使用统一 view index。
- Cluster 不解释 view 是 main、VSM、stereo、reflection 或其它业务用途。
- 不通过 C# 外层循环把完整单-view pipeline 重跑 N 次来冒充 multi-view。
- candidate 和后续 work 必须携带 view identity/index。

## Relations
[[architecture/cluster/Cluster-Scope]] · [[architecture/features/Virtual-Shadow-Maps]]

## 实例集合与输出层

游戏通过独立的 `InstanceSet` 值组件指定网格所属集合；相机用 `CameraInstanceSet` buffer 保存所选集合。
场景 codec 分别序列化这两类值数据。
`MeshRenderExtractor` 将集合编号提取为 Rendering world 中的 `InstanceSet` 值组件。
用户几何沿自己的提取流程写入同一组件。
`RenderView.InstanceSets` 保存该视图选择的集合编号；空列表选择全部集合。

Cluster 复用同一实例批次，按集合组织实例行索引，再在各集合内部形成至多 64 行的空间组。
集合成员变化时更新索引和组范围；变换变化时由 GPU 更新已有组的包围范围。
剔除入口先为组计算参与视图掩码，然后仅为这些视图展开实例的 BVH。
集合编号留在组描述中，后续工作记录携带实例行和普通视图索引。

`ClusterPipelineOutputDescription.ViewCount` 指定普通相机数量，`LayerCount` 指定输出数组层数。
`RenderView.OutputLayer` 指定深度、可见性和颜色数组的输出层，默认值为视图索引。
例如两个普通相机可配置 `viewCount: 2, layerCount: 1`，并将各自的 `OutputLayer` 设为 0。可见性记录保留实际视图索引，材质通过
`SurfacePixelInput.ViewIndex` 取得自己的相机；`PixelCoord.z` 表示实际输出层。
游戏使用各视图自己的投影矩阵设置 FOV、近平面和深度区间。
光栅深度比较决定共享输出中的可见结果。

游戏可用独立集合和普通相机表达手臂、武器或其它覆盖视图；这些实例的世界变换
同时提供普通 VSM 投射几何。阴影功能沿通用视图执行和世界空间采样链消费这些数据。
