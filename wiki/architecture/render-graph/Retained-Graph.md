# Retained Graph

## Definition
RenderGraph 的结构长期存在。正常帧更新数据，不重新描述或重新建立整张图。

## Invariants
- 区段、pass、资源和关系具有跨帧身份。
- 结构改变只能通过显式编辑接口发生。
- 数据变化不得被提升成结构变化。
- 局部修改不得使无关身份失效。

## Regions

`GraphRegionId` 表示持久树中的有序位置。编辑通过 `AddRegion` 建立区段，pass 添加接口的
`region` 参数选择位置，默认值选择根区段。区段内后来安装的 pass 仍在该区段的编号区间
执行；嵌套区段和 pass 按有序子树展开。资源使用者继续先服从显式顺序关系，其余使用者
按声明序号排列。具体接口、编号和启用规则由 [[Regions]] 持有。

`RenderGraphStatistics.AnalyzedPassCount` 和 `RebuiltRowCount` 分别记录当帧进入分析的
pass 数及重建行数。静态帧复用分析，两项计数为零；局部序号重编沿已有变化记录触发
对应 pass 及共享资源使用者的分析。

## Relations
[[Regions]] · [[Structural-Changes]] · [[Frame-Data]] · [[Graph-Identity]] · [[Graph-Versioning]] · [[Incremental-Reconciliation]]