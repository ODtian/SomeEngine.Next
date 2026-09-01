# Resident Ranges

Cluster 维护 PageHeap 当前可被 traversal/raster 发现的 resident byte intervals，并增量 merge/split。

Persistent graph pass 每 frame 对这些真实 ranges 增加普通 Buffer Read；fresh page upload 只 Write 新 range。RenderGraph 因而能区分无关 range，不把整个 PageHeap 当成单一 hazard。

不建立额外 resource-access-list 架构类型。

参见 [[architecture/render-graph/Resource-Access]] · [[Page-Heap]]。