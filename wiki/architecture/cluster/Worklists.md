# Worklists

所有 candidate、visible、append、binning、fault 与其它设备 worklists 都有预配置 fixed capacity 与清楚的 overflow semantics。

每个 worklist 只保留保证 memory safety 所需的最小边界逻辑；容量与质量策略由上层配置。

层级遍历与 cluster 剔除在每轮的一个 `CullClusterWork` dispatch 内消费 GPU 工作队列。
`CullWork` 随持久图保存单份 node、cluster、就绪标记及计数缓冲，四轮主视图/目标视图工作顺序复用这些缓冲。
队列容量来自全帧 `MaxCandidates`，worker 数量按实际 BVH 节点和实例工作范围更新，
并由设备报告的 SIMD lane 数与单组线程上限派生的预算约束。单个实例的宽层级也能由多个 worker 消费。

node 消费者以 CAS 领取已发布就绪的父节点前缀，每次至多十六个父节点，每个父节点检查四个子切片。生产者先预留容量，
把实际接受的子节点计入剩余工作，再写入 payload 并发布就绪标记；消费父节点后的计数递减发生在子节点发布之后。
没有就绪 node 前缀时，worker 以 CAS 领取六十四个 cluster 的已发布完整批次；剩余 node 为零时领取最后的部分批次。
已消费的就绪标记恢复为零。新分配和中断消费后的下一帧执行队列初始化，成功完成的各轮直接复用缓冲。

主轮按之前的 HiZ 延迟遮挡工作，后轮按当前 HiZ 消费延迟工作并保留可见窗口追加偏移。
容量不足把实际拒绝工作写入全帧 `WorkStatus`；剩余 node 只统计接受的工作，因此消费者仍能排空已接受内容并交付明确失败。

工作领取、发布和批次回收参考固定 Epic 提交
[`396c9f059903aed5fec78ecd3d437a40c6415368` 的 NaniteHierarchyTraversal.ush](https://github.com/EpicGames/UnrealEngine/blob/396c9f059903aed5fec78ecd3d437a40c6415368/Engine/Shaders/Private/Nanite/NaniteHierarchyTraversal.ush)
与 [NaniteClusterCulling.usf](https://github.com/EpicGames/UnrealEngine/blob/396c9f059903aed5fec78ecd3d437a40c6415368/Engine/Shaders/Private/Nanite/NaniteClusterCulling.usf)。

参见 [[Overflow]] · [[architecture/validation/Memory-Safety-Guards]]。
