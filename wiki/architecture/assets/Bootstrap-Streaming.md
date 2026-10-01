# Bootstrap Streaming

## Definition
Bootstrap 是某项业务操作所需的最低有效表示。只有它就绪，内容才可交付给该操作使用；后续数据提高细节。对象描述创建完成与最低可绘制表示就绪是两个明确的完成条件。

## Invariants
- `TextureBlock.CreateAsync` 在最粗 mip 的完整像素取得后返回 Texture，调用方可以直接使用其最低采样表示。
- `ClusterMeshBlock.CreateAsync` 取得 BVH 和 LowestDetailPageIndices 指定的几何页后返回。后续细节显式取得，再经既有 Game world → Rendering world 路径发布。
- 普通描述存在不代表几何数据已加载；完整的创建入口必须满足其承诺的最低可用表示。
- Bootstrap 在其最低业务表示被使用期间不可被普通细节驱逐策略移除。
- refinement 失败时保留当前有效表示，不切换到 correctness fallback。
- 正常首用不得因等待细节数据而同步阻塞 I/O。

## Relations
[[Texture-Bootstrap]] · [[architecture/cluster-streaming/Page-Heap]]
