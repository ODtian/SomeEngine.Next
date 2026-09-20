# Virtual Shadow Maps

VSM 是独立 rendering feature，拥有自己的 page table、atlas/cache、marking、allocation 与 shadow policy。

VSM 构造普通 unified render views，并通过 geometry pipeline 能力使用 Cluster；Cluster 不知道这些 views 表示 shadow。VSM counters 和策略不进入 Cluster core ABI。

参见 [[architecture/rendering/Multi-View]] · [[architecture/cluster/Cluster-Scope]]。