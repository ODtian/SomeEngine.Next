# Multi-View

## Definition
View array 是所有 geometry pipeline 的一等输入。

## Invariants
- traversal、LOD/cull、deform、raster、shade 与 tessellation 使用统一 view index。
- Cluster 不解释 view 是 main、VSM、stereo、reflection 或其它业务用途。
- 不通过 C# 外层循环把完整单-view pipeline 重跑 N 次来冒充 multi-view。
- candidate 和后续 work 必须携带 view identity/index。

## Rendering world

视图以 RenderWorld 中的 `RenderView` 组件提供。`RenderViewTarget` 通过 `ViewTargetKind.Screen`、`ViewTargetKind.ShadowPages` 指明目标和目标索引，`RenderPreviousView` 在同一个实体上保存上一帧投影。几何管线按目标类型排序视图，所有阶段沿用统一的视图索引。

`VirtualShadowViews` 在准备阶段创建和更新阴影视图实体。当前阴影投影和已提交历史由每帧数据区分；提交观察者在整帧提交后接受历史。光栅需要的页表、HiZ 与目标资源通过 feature 资源名取得。

## Relations
[[architecture/cluster/Cluster-Scope]] · [[architecture/features/Virtual-Shadow-Maps]]