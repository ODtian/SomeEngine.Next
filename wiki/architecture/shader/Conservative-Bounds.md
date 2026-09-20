# Conservative Bounds

## Definition
任何可能改变 geometry position 的 stage 必须为依赖它的 culling 提供足够 conservative 的有限 bounds，或在 pipeline configuration 时声明相应 culling 不可使用。

## Invariants
- 框架不猜用户算法内部位移范围。
- bounds 能力在 pipeline creation/configuration 时确定。
- 热路径不逐 item 尝试 bounds，再临时切换到 fallback。
- culling 不允许因为用户位置变化产生 false negative。

## Relations
[[User-Stage-Interfaces]] · [[architecture/cluster/LOD-DAG]]