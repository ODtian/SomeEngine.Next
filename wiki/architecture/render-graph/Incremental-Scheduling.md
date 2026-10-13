# Incremental Scheduling

## Definition
排序关系变化后只修复受影响的 DAG 区域。

## Invariants
- 自动选择队列的 pass 先减少前驱交接，再沿兼容的计算、复制链减少消费者队列的交接；显式队列选择和命令类别确定链的边界。消费者队列或依赖编辑沿这条链更新选队列，按执行顺序让消费者读取前驱的新队列。
- 从 predecessor/successor 发生变化的 pass 开始。
- 沿真正受影响的相邻节点传播。
- 已缓存结果与新结果一致时停止传播。
- 不因为一个局部 edge 变化重新排序整个 graph。

## Current design
具体动态拓扑算法可替换；本页只固定局部更新语义。

资源的版本顺序分析共用当前显式依赖的祖先搜索结果。每轮依赖分析按实际查询的
pass 计算祖先位集，资源通过与自身使用者相交获得排序约束，随后按声明顺序处理
同时就绪的使用者。搜索工作区随下一轮分析刷新，显式依赖仍由原图数据持有。
包含循环的关系沿原资源投影处理，资源版本及有效 pass 的循环检验保持原行为。

调用方按已知工作频率声明命令回放。接近每帧变化的 temporal history 参数与设备 query
使用当帧记录，主体中的固定资源与拓扑使用可回放记录单元。相邻单元按 `RepeatCommands` 分段，
图的原 command context pool 持有命令存储，GPU completion 保持已提交列表与资源的生命周期。
同 command slot 的 attachment descriptor arena 随下一轮 Begin 重置，其已记录列表可沿原缓存回放。

## Relations
[[Incremental-Reconciliation]] · [[Incremental-Hazards]]
