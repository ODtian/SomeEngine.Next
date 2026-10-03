# Cluster Retained Graph Usage

## Definition
Cluster 的生产渲染结构长期安装在 RenderGraph 中；普通帧只更新执行所需的数据和资源范围，不重新建立整套 traversal、cull、binning、deform、raster、HiZ 与 shade passes。

## Invariants
- pipeline 或 feature 配置真正改变时才进行结构编辑。
- view array、instance data、resident ranges、输出目标、counts 与阶段数据属于帧数据。
- 当帧区段承接逐帧重建的局部工作；主路径通过显式编辑维护持久区段。
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

## Raster regions

主视图的两轮光栅与目标视图的两轮光栅分别占据一个持久区段。每轮内部依次包含：

| 子区段 | 更新方式 |
| --- | --- |
| 工作分箱 | 固定的 reset、count、reserve、scatter；当帧数据更新 dispatch 与常量 |
| 细分组 | 按 split pipeline 与资源绑定组成的 split key 调和子区段 |
| 最终 patch 分箱 | 存在细分组时安装，最后一个细分组移除时删除 |
| 光栅箱 | 按光栅箱键调和；箱内包含对应的 dice、软件光栅与硬件光栅 |
| 主视图可见性解析 | 把软件光栅累积的可见性写入主视图纹理 |

每个细分组依次包含 reset、seeds、split。seeds 按光栅箱键调和，各箱的输入与上传
数据随对应区段保存。split 保存当前代表材质和单箱/多箱选择；种子新增时沿用仍存在的
代表材质，代表箱变化时替换 split 工作。细分层级通过共享的计数、patch 和间接参数缓冲
建立依赖。主体图、各箱位置与子树删除由 RenderGraph 的区段树持有。

调和返回的各轮映射在图编辑提交后替换当前映射。上传数据通过既有上传编辑事务管理。
光栅阶段开关通过区段启用设置控制；主视图工作分箱的 reset 和 reserve 继续为零实例帧
产生有效参数，可见性解析继续产生背景深度。目标视图阶段通过整轮区段控制。

页和 BVH 上传属于当帧工作，放在主几何开头的 `Cluster residency uploads` 当帧区段。
它们的写入与遍历、剔除、分箱和光栅的普通读取形成依赖。每个光栅阶段的内部顺序、
主视图 HiZ 和第一轮细分计数复制也由真实资源访问与区段位置建立；光栅相关显式顺序边
数量为零。

主视图和目标视图的首次遍历与延迟遍历也各自占据一个区段。流送增加 BVH 层级时，
新增层仍追加在对应遍历区段中，随后剔除与光栅继续按原来的区段位置读取结果。

## Behavior evidence

`FrameStageRenderingTests` 通过硬件适配器上的完整 Cluster 着色组合，覆盖 D3D12 与 Vulkan。
VSM 开关两种状态的 64×64 RGBA16F 输出逐字节比较迁移前的图像；Standard 与 Unlit
运行中互换后，Temporal 输出与最终材质的全新安装逐字节一致。测试只经公开纹理输出、
图复制命令与缓冲映射观察结果。迁移基线的提交、适配器与采样参数由
`tests/SomeEngine.Render.Cluster.Tests/FrameStageImages/README.md` 记录。

`RasterRegionRenderingTests` 使用完整 Cluster 与 VSM 组合，通过硬件适配器上的 D3D12
和 Vulkan，覆盖软件光栅与硬件光栅。普通材质和细分材质的新增、删除、替换，以及共享
细分组首个材质的移除、split shader 替换，都逐字节比较最终材质的全新安装颜色与深度。
场景使用内联生成的八面体和 shader 声明的线性曲面，通过公共帧指标确认细分工作完成。
单箱材质更新还通过公共 `AnalyzedPassCount` 验证 64 个独立资源 pass 保持分析复用。

## Material inputs

Cluster 按 `(Material, 属性编号)` 持有一个 `GraphParameterInput`。使用该属性的 pass
绑定同一个输入，帧录制为每个输入设置一次值。参数绑定同时声明纹理和缓冲的真实访问；
纹理上传与最终状态转换通过输入连接到它的全部使用者，连接关系由 RenderGraph 持有。

材质箱的代表材质变化时，图编辑同时替换参数绑定和访问。细分组的共享 split pass
沿用该方式切换到当前种子材质。编辑完成后，Cluster 按图中的实际参数绑定保留材质输入。

## Relations
[[architecture/render-graph/Retained-Graph]] · [[architecture/render-graph/Regions]] · [[architecture/render-graph/Frame-Data]] · [[Cluster-Scope]]
