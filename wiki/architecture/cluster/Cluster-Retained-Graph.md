# Cluster Retained Graph Usage

## Definition
Cluster 的生产渲染结构长期安装在 RenderGraph 中；普通帧只更新执行所需的数据和资源范围，不重新建立整套 traversal、cull、binning、deform、raster、HiZ 与 shade passes。

## Invariants
- pipeline 或 feature 配置真正改变时才进行结构编辑。
- view array、instance data、resident ranges、输出目标、counts 与阶段数据属于帧数据。
- frame extension 只用于真正一次性的额外工作，不能成为 Cluster 主路径。
- Cluster 不建立 RenderGraph 之上的第二套图描述体系。

## Relations
[[architecture/render-graph/Retained-Graph]] · [[architecture/render-graph/Frame-Data]] · [[architecture/render-graph/Extension-Points]] · [[Cluster-Scope]]
