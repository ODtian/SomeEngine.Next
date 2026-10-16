# Worklists

所有 candidate、visible、append、binning、fault 与其它设备 worklists 都有预配置 fixed capacity 与清楚的 overflow semantics。

每个 worklist 只保留保证 memory safety 所需的最小边界逻辑；容量与质量策略由上层配置。

层级遍历与 cluster 剔除在每轮的一个 `CullClusterWork` dispatch 内消费 GPU 工作队列。
`CullWork` 随持久图保存单份 node、cluster、就绪标记及计数缓冲，四轮主视图/目标视图工作顺序复用这些缓冲。
队列容量来自全帧 `MaxCandidates`，worker 数量按实际 BVH 节点和实例工作范围更新，
并由设备报告的 SIMD lane 数与单组线程上限派生的预算约束。单个实例的宽层级也能由多个 worker 消费。

node 消费者以一次原子累加领取十六个父节点的位置范围，在组内保存下一位置与范围末尾。
每轮消费范围中已发布就绪的连续前缀，其余位置随生产者发布后继续消费；每个父节点检查四个子切片。
每组按已发布节点的实际 view mask 把线程分配给 parent、child 和 view；单 view 使用十六 parent × 四 child，
多个 view 共享子切片测试，并在组内合并可见 mask 与 streaming priority 后仍发布一个压缩节点。
每个 lane 排空自己分配的实际 view mask，多个 view 的投射、LOD 与 HiZ 在同一 wave 同时执行。
同 child 的 world bounds 与 LOD 派生由 wave 中一个 lane 计算并广播；mask 和 priority 先在 wave 内合并，
跨越多个 wave 的 child 再做组内合并。根工作的 instance × view 块使用同样的 wave 合并。
根工作以六十四实例和三十二 view 为批次，按实际 view 数拆实例子块并并行 view 测试，合并后保留一个 root。
CPU dispatch 使用 `ClusterRenderContext.GetViewWorkGroupCount`，设备 producer 使用 `cluster_view_work.slang` 的
`ClusterViewWorkGroups`：X 为 `ceil(instanceCount / 64)`，view 组数为每完整三十二 view 的十六组加尾部 view 数
（至多十六）的下一二次幂。Y 为 `min(viewGroups, 65535)`，Z 为至少一的 `ceil(viewGroups / 65535)`；
shader 以 `groupID.y + groupID.z * 65535` 读取 view 组。目标根参数由现有 view compaction producer 维护。

叶切片按实际 cluster 和 view 逐项生成 `ClusterCandidate`，在当前 wave 内直接交给共同的
`CullClusterCandidateData` 完成 LOD、目标过滤、HiZ 与可见窗口处理。候选值在本次调用内消费，
`CandidateCount` 累加实际工作量并提供既有帧指标。主视图与目标视图共用这条数据通路。
延期恢复把实际 cluster payload 写入共享队列；既有索引入口加载候选后调用同一个剔除函数。
`CullWork.Arguments` 的第 28 字节保存延期队列实际条目数，领取批次与完成判断读取该值。
累计工作量和本轮排队数量各自表达对应的数据。

材质静态组合的 `ConservativeBounds` 按实例使用的材质序列汇总。光栅阶段解析时维护一份
`MaterialBounds` 缓冲，实例沿既有材质序列 offset 读取这一能力。根播种、层级与 cluster
筛选各自把该能力应用到当前实例的视图：有限序列使用包围盒、LOD 与 HiZ；无界序列保守展开。
空间组包围盒同时携带组内是否全部有限，逐实例筛选再选择各自能力。材质或资源组合变化后，
场景包围盒随光栅版本刷新，提交成功时记录该版本。

目标光栅窗口枚举使用 `PolicyRasterBoundsWindow` 返回的请求页交集，再在该交集中按
硬件或软件窗口大小展开。投影区域与已请求区域共同决定实际接受的窗口。

生产者先预留容量，
把实际接受的子节点计入剩余工作，再写入 payload 并发布就绪标记；消费父节点后的计数递减发生在子节点发布之后。
当前 node 范围等待发布时，worker 以一次原子累加领取六十四个延期 cluster 的批次位置，
在组内保存该位置直到批次发布完整；剩余 node 为零时消费最后的部分批次。
每轮领取的位置在本轮只使用一次。node 写入组内 payload 时清除其就绪标记，cluster 批次开始消费时清除批次标记；
node 子工作发布后的设备屏障随后覆盖对应清除，cluster 组在下一轮工作选择前完成组内同步。
新分配和中断消费后的下一帧执行队列初始化，成功完成的各轮直接复用缓冲。
就绪与计数由 coherent 工作缓冲读取；实际领取工作后在 payload 读取前执行设备可见性屏障。
每组统一判断剩余 node 为零、自己领取的 cluster 批次位置达到生产末尾后结束。

主轮按之前的 HiZ 延迟遮挡工作，后轮按当前 HiZ 消费延迟工作并保留可见窗口追加偏移。
容量不足把实际拒绝工作写入全帧 `WorkStatus`；剩余 node 只统计接受的工作，因此消费者仍能排空已接受内容并交付明确失败。

工作领取、发布和批次回收参考固定 Epic 提交
[`396c9f059903aed5fec78ecd3d437a40c6415368` 的 NaniteHierarchyTraversal.ush](https://github.com/EpicGames/UnrealEngine/blob/396c9f059903aed5fec78ecd3d437a40c6415368/Engine/Shaders/Private/Nanite/NaniteHierarchyTraversal.ush)
与 [NaniteClusterCulling.usf](https://github.com/EpicGames/UnrealEngine/blob/396c9f059903aed5fec78ecd3d437a40c6415368/Engine/Shaders/Private/Nanite/NaniteClusterCulling.usf)。

参见 [[Overflow]] · [[architecture/validation/Memory-Safety-Guards]]。
