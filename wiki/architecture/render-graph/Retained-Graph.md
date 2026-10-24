# Retained Graph

## Definition
RenderGraph 的结构长期存在。正常帧更新数据，不重新描述或重新建立整张图。

## Invariants
- pass、资源和关系具有跨帧身份。
- 结构改变只能通过显式编辑接口发生。
- 数据变化不得被提升成结构变化。
- 局部修改不得使无关身份失效。

## Relations
[[Structural-Changes]] · [[Frame-Data]] · [[Graph-Identity]] · [[Graph-Versioning]] · [[Incremental-Reconciliation]]