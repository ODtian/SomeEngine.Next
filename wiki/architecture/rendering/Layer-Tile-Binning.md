# Layer Tile Binning

`LayerTileBinningPasses` 把几何剔除的可见 cluster 与 tile 窗口交给共享 `ScreenBinning`，再生成每个活跃几何阶段的 tile 工作。几何使用方实现编译期接口 `ILayerTileClusterSource`，提供可见 cluster 下标、tile 窗口、tile 下标，以及 cluster 是否包含指定 Vertex、Domain、Visibility 组合。Cluster 的使用方沿现有 `ClusterMaterialRanges` 和材质到光栅阶段的映射读取阶段归属。

阶段下标是本次活跃几何组合表的紧凑下标。使用方通过这张表映射既有几何阶段 ID，分配维度为活跃组合数与列表容量；同一个 cluster 可以属于多个组合。

共享分箱展开窗口，得到每个 tile 的 cluster 列表段。层使用方的 `FillScreenItems` 调用 `ScreenBinningFillUnordered`，在扫描完成后递减原 cell 计数取得唯一写入位置，逐项直接写入 cluster 下标；列表顺序来自 GPU 的实际执行顺序。`MarkLayerRasterStages<TSource>` 为各阶段标记列表归属，共享扫描给出每个阶段、每个 tile 的区间；`LayerFillStageClusters` 写入实际可见 cluster 下标。随后扫描非空区间标志，生成紧凑 `LayerStageWork`、各阶段的工作区间和间接派发参数。分组沿用共享分箱的列表顺序，最终合成顺序由片元排序决定。

`StageClusters` 保存可见 cluster 下标；`StageTileRanges` 保存各阶段与 tile 的 `(起点, 数量)`。`StageWorks` 的每项包含 tile、阶段、cluster 起点和数量；`StageWorkRanges` 为每个阶段提供工作区间。`StageDispatch` 每个阶段保存一个 `uint3`，驱动一次间接派发。

光栅入口通过 `LoadLayerStageWork(stage, groupId)` 读取工作项，并把它交给 `TileClusterInput`。`GetClusterCount` 和 `ReadCluster` 遍历该工作项的几何；`LayerVisibilityOutput.Tile` 使用工作项的 tile。每个通过测试的片元追加到该 tile 的片元块链，继而进入统一着色和合成。

C# helper 为静态注册函数，返回图 ID。`LayerComposition` 在图编辑提交成功后保存结果，并显式传入条目数、条目容量、tile 数、活跃阶段数和列表容量填写帧参数。容量与阶段表改变时，使用方显式编辑对应资源和共享扫描工作。共享分箱的诊断读回由使用方持有，并在读回送达的 prepare 阶段处理容量需求。

`LayerTileBinningTests` 从公共图入口提交 1025 个剔除结果，核对两个跨工作组 tile 中各几何阶段得到的完整 cluster 集合，以及工作区间和间接参数。
