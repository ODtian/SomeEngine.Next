# 特殊不变量数据  Parent: [[Generated-Job-API]] 

SG 将 `ref Parent<D>` 或 Parent writable Span 识别为 deferred hierarchy write，而不是普通无约束 component write。generated adapter 为整个逻辑 owner 声明对应 domain 的 Parent read/write 与 validation/finalization access，在 body 前保存其 owned rows 的 preimage，在全部 packets结束后验证最终 one-parent、liveness、self-parent和acyclic forest；body fault、cancel或验证失败时在释放 owner前恢复 preimage并 fault/cancel handle。不同 typed domains可并行；同一 domain 的多个 deferred writers只有在同一逻辑 owner内合并，或 SG 能证明其最终 forest validation互不影响时才并行。

这种 ref/span 只改变 canonical Parent并发布普通 change epoch，不在 worker body 内随机写 old/new parent 的 Children。其 inverse transition由后续 hierarchy maintenance system批量应用。Parent component的结构性 add/remove和 placement-capable操作仍使用 typed API/command writer，以便记录 absence、old value和精确 sibling placement。

SG 和 runtime 继续拒绝：

- writable Children/Depth/HierarchyLink；
- writable relation source/target；
- parallel writable random lookup 未带可证明 partition；
- worker job 中不支持的 managed/thread-affine access。

这些 derived/protected 修改通过 ECS 内部 maintenance 或 typed hierarchy/relation command writer完成；用户永远拿不到 writable Children 或 relation endpoint ref/span。

