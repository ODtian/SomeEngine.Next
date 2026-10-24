# Multi-View

## Definition
View array 是所有 geometry pipeline 的一等输入。

## Invariants
- traversal、LOD/cull、deform、raster、shade 与 tessellation 使用统一 view index。
- Cluster 不解释 view 是 main、VSM、stereo、reflection 或其它业务用途。
- 不通过 C# 外层循环把完整单-view pipeline 重跑 N 次来冒充 multi-view。
- candidate 和后续 work 必须携带 view identity/index。

## Relations
[[architecture/cluster/Cluster-Scope]] · [[architecture/features/Virtual-Shadow-Maps]]