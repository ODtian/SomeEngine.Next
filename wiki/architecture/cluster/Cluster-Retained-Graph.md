# Cluster Retained Graph Usage

## Definition
Cluster 的生产渲染结构长期安装在 RenderGraph 中；普通帧只更新执行所需的数据和资源范围，不重新建立整套 traversal、cull、binning、deform、raster、HiZ 与 shade passes。

## Invariants
- pipeline 或 feature 配置真正改变时才进行结构编辑。
- view array、instance data、resident ranges、输出目标、counts 与阶段数据属于帧数据。
- frame extension 只用于真正一次性的额外工作，不能成为 Cluster 主路径。
- Cluster 不建立 RenderGraph 之上的第二套图描述体系。

## Material inputs

Cluster 按 `(Material, 属性编号)` 持有一个 `GraphParameterInput`。使用该属性的 pass
绑定同一个输入，帧录制为每个输入设置一次值。参数绑定同时声明纹理和缓冲的真实访问；
纹理上传与最终状态转换通过输入连接到它的全部使用者，连接关系由 RenderGraph 持有。

材质箱的代表材质变化时，图编辑同时替换参数绑定和访问。细分组的共享 split pass
沿用该方式切换到当前种子材质。编辑完成后，Cluster 按图中的实际参数绑定保留材质输入。

## Relations
[[architecture/render-graph/Retained-Graph]] · [[architecture/render-graph/Frame-Data]] · [[architecture/render-graph/Extension-Points]] · [[Cluster-Scope]]
