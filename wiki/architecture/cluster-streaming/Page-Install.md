# Page Install

## Sequence
1. 为新 page 分配尚未被 traversal 发现的 free range。
2. async I/O / decode 写 staging。
3. `ClusterPageFixups` 按 reduction group 补齐自身页范围与粗层依赖。整个组页可用后，一起写入该组 hierarchy parts 的页地址。
4. 同帧 RenderGraph copy staging → PageHeap/BVH，并在 page upload 后执行共享的 streaming leaf flag pass。父 cluster 的 STREAMING_LEAF 与父 hierarchy part 的 MinLODError 符号反映当前细组可用性；FULL_LEAF 保留构建时的含义。
5. 主视图与其它消费者在此更新后遍历。同帧 page/BVH upload 和 flag pass 成功提交后，提交驻留状态。

项目页中的 96B Cluster metadata 直接采用 GPU 表示，用户 vertex blob 保持不透明。加载从认证磁盘页取得完整字节，转换 hierarchy 引用中的局部页编号为 PageHeap 地址；fixup 地址由显式组记录定位。

`ClusterRenderResources.LodChanges` 在 prepare 期间提供受影响组的 mesh、root 与对象空间 LOD sphere。成功提交消费该批；执行取消时同批在下一帧继续提供。

## Invariants
page bytes 必须先可读，再使 leaf 指向它。上传 fresh range 可以与旧 frame 对其它 resident ranges 的读取并行。

## Relations
[[Page-Heap]] · [[BVH-Leaf-Patching]] · [[Page-Eviction]]
