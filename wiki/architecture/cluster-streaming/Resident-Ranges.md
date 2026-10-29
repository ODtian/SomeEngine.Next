# Resident Ranges

Cluster 维护 PageHeap 当前可被 traversal/raster 发现的 resident byte intervals，并增量 merge/split。

Persistent graph pass 每 frame 对这些真实 ranges 增加普通 Buffer Read；fresh page upload 只 Write 新 range。RenderGraph 因而能区分无关 range，不把整个 PageHeap 当成单一 hazard。

Streaming leaf flags 的更新 pass 复用当前 frame 合并后的 resident ranges 声明 ReadWrite，
与 traversal/raster 共享有效页区间和空洞。新载入页的完整分配内容先由上传 pass 定义，
leaf flags 随后按各自的 cluster 地址写入；资源分析沿合并页范围建立依赖。

不建立额外 resource-access-list 架构类型。

参见 [[architecture/render-graph/Resource-Access]] · [[Page-Heap]]。
