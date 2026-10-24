# Page Eviction

## Sequence
1. patch leaf → fault marker。
2. future traversal 不再发现旧 page。
3. 已开始的旧 frame 仍可读取旧 PageHeap range。
4. 取得旧 range 已提交访问对应的 [[architecture/render-graph/Range-Completions|Range Completions]]。
5. 相关 completions 全部结束后 range 才能回到 free allocator。

## Invariants
- 不通过 CPU 等整个 Cluster 子系统静止。
- 不在旧 reader 结束前复用 range。

## Relations
[[Page-Install]] · [[BVH-Leaf-Patching]] · [[Resident-Ranges]]