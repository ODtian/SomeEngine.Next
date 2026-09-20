# World Structure

World 的结构变化通过明确结构边界更新 entity records、chunks 与相关 side data；query/change tracking 观察一致的结构 epoch。

结构数据的并发与事务细节由 [[Structural-Transactions]]、[[Change-Tracking]] 和 [[ECS-Admission-and-Ranges]] 分别定义，本页不复制它们。
