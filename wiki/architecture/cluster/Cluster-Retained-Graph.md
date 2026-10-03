# Cluster Retained Graph Usage

## Definition
Cluster 的生产渲染结构长期安装在 RenderGraph 中；普通帧只更新执行所需的数据和资源范围，不重新建立整套 traversal、cull、binning、deform、raster、HiZ 与 shade passes。

## Invariants
- pipeline 或 feature 配置真正改变时才进行结构编辑。
- view array、instance data、resident ranges、输出目标、counts 与阶段数据属于帧数据。
- frame extension 只用于真正一次性的额外工作，不能成为 Cluster 主路径。
- Cluster 不建立 RenderGraph 之上的第二套图描述体系。

## Frame stages

应用组合根 `RuntimeApplication` 在安装时建立帧阶段骨架。顶层区段按下表顺序创建，
Feature 在组合根给出的区段中安装自己的 pass。资源使用者先服从显式顺序关系，
其余使用区段树的声明顺序；区段位置与资源访问共同给出执行依赖。

| 区段 | 当前工作 |
| --- | --- |
| 上传 | 帧上传复制的接入位置 |
| 场景 | Lighting 网格与 cookie 初始化 |
| 主视图几何 | Cluster 输入初始化、实例复制、场景包围盒、遍历、两轮剔除与光栅、HiZ |
| 主视图深度之后 | VSM 初始化、页标记、分配、清除与目标视图准备 |
| 目标视图几何 | Cluster 目标视图两轮几何，中间为目标上下文的遮挡构建区段 |
| 目标视图几何之后 | VSM 最终页 HiZ、光栅完成与接收者掩码复位 |
| 着色 | Cluster 着色分箱、背景解析与材质箱 |
| 后处理 | 依次包含 Temporal、呈现、UI 三个子区段 |
| 反馈 | VSM 动态分辨率与帧端点、Cluster 页缺失与帧指标回读 |

`ClusterGraphRegions` 把主视图几何、目标视图几何、着色、反馈四个区段交给 Cluster。
材质替换和遍历增长产生的 pass 仍安装在相应区段内；后处理维持安装时的位置。
主视图第一轮候选指标在对应候选缓冲复用前读取，位于主视图几何区段；最终帧指标
与页缺失结果在反馈区段读取。

Cluster 在构造时按顺序建立目标视图第一轮几何、`IntermediateOcclusionRegion`、
目标视图第二轮几何三个子区段。目标上下文把中间 HiZ 工作放入公开的遮挡区段，
最终 HiZ 放入组合根给出的目标视图几何之后区段。`ClusterRenderContext` 持有目标资源
绑定、数量、尺寸与遮挡策略；VSM 内部的显式顺序关系继续由 VSM 维护。

## Behavior evidence

`FrameStageRenderingTests` 通过硬件适配器上的完整 Cluster 着色组合，覆盖 D3D12 与 Vulkan。
VSM 开关两种状态的 64×64 RGBA16F 输出逐字节比较迁移前的图像；Standard 与 Unlit
运行中互换后，Temporal 输出与最终材质的全新安装逐字节一致。测试只经公开纹理输出、
图复制命令与缓冲映射观察结果。迁移基线的提交、适配器与采样参数由
`tests/SomeEngine.Render.Cluster.Tests/FrameStageImages/README.md` 记录。

## Material inputs

Cluster 按 `(Material, 属性编号)` 持有一个 `GraphParameterInput`。使用该属性的 pass
绑定同一个输入，帧录制为每个输入设置一次值。参数绑定同时声明纹理和缓冲的真实访问；
纹理上传与最终状态转换通过输入连接到它的全部使用者，连接关系由 RenderGraph 持有。

材质箱的代表材质变化时，图编辑同时替换参数绑定和访问。细分组的共享 split pass
沿用该方式切换到当前种子材质。编辑完成后，Cluster 按图中的实际参数绑定保留材质输入。

## Relations
[[architecture/render-graph/Retained-Graph]] · [[architecture/render-graph/Regions]] · [[architecture/render-graph/Frame-Data]] · [[Cluster-Scope]]
