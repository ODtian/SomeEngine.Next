# Extension Points

## Definition
Extension point 是外部 feature 向长期图的明确插入边界。

## Invariants
- production feature 的长期 pass 安装一次，frame 只更新数据。
- 真正一次性工作可以在明确 extension point 中插入临时 pass。
- 不允许借 extension point 把主 renderer 重新变成每帧建图。
- feature 自己维护业务 identity 到 graph IDs 的映射并显式 Create/Remove/Update。

## Relations
[[Retained-Graph]] · [[Structural-Changes]]