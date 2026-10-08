# Page Eviction

## Sequence
1. 按受影响 reduction group 将细层 hierarchy parts 一起写为 fault marker，恢复父 cluster 的 STREAMING_LEAF 与父 part 的当前叶状态。仍有已驻留依赖细页的粗层页保留；页池淘汰沿 LRU 继续选择可释放页。
2. future traversal 不再发现旧 page。
3. 已开始的旧 frame 仍可读取旧 PageHeap range。
4. 取得旧 range 已提交访问对应的 [[architecture/render-graph/Range-Completions|Range Completions]]。
5. 相关 completions 全部结束后 range 才能回到 free allocator。

## Invariants
- 不通过 CPU 等整个 Cluster 子系统静止。
- 不在旧 reader 结束前复用 range。

## Relations
[[Page-Install]] · [[BVH-Leaf-Patching]] · [[Resident-Ranges]]
