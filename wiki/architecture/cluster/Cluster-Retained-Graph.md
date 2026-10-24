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
| 主视图几何 | Cluster 输入初始化、场景包围盒、遍历、两轮剔除与光栅、HiZ |
| 主视图深度之后 | VSM 初始化、页标记、分配、清除与目标视图准备 |
| 目标视图几何 | Cluster 目标视图两轮几何，中间为目标上下文的遮挡构建区段 |
| 目标视图几何之后 | VSM 最终页 HiZ、光栅完成与接收者掩码复位 |
| 着色 | Cluster 着色分箱、材质箱与背景解析 |
| 后处理 | 依次包含 Temporal、呈现、UI 三个子区段 |
| 反馈 | VSM 动态分辨率与帧端点、Cluster 页缺失与帧指标回读 |

实例属性和批元数据通过 RenderGraph 帧上传写入单份持久缓冲，场景位置从 Rendering world
变换组件读取。

`ClusterGraphRegions` 把主视图几何、目标视图几何、着色、反馈四个区段交给 Cluster。
材质替换产生的 pass 仍安装在相应区段内；后处理维持安装时的位置。
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

调和返回的各轮映射、shader 常量与缓冲写入范围随编辑数据一起在图提交后替换当前值。
shader 常量按类型分配固定的 `GraphBufferSlice`，静态字节由图数据中的合并字节持有，
随显式编辑改页。当帧整段上传后更新 scene 与两种 seed 值，已安装的 slice 继续用于绑定。
已暂存的 `RenderView` 与已编码的视图数组分别持有输入和矩阵派生数据；视图值或数据原点改变时重算对应矩阵。
实例数量、流送质量、目标编号和历史字段逐帧刷新，历史仍按成功提交推进。
主视图几何工作和目标视图几何分别由祖先区段启用控制，包含对应轮次的全部
光栅箱、细分组与工作分箱。输入计数初始化、可见性与深度目标清除保留在主视图工作
之前，零实例帧因此仍提供有效的计数、背景深度和后续 Feature 输入。
空几何帧在主视图前段清空基础与额外接收层，使后续 VSM 读取已定义且 `PRESENT=0` 的接收数据。

页堆与 BVH 是驻留 epoch 对应的图持久缓冲。CPU 流送数据由驻留业务保存；
当帧变化通过 `WriteBuffer` 登记范围，图的持久上传 pass 在上传区段完成复制，
随后遍历、剔除、分箱与光栅使用这些缓冲。每个光栅阶段的内部顺序、
主视图 HiZ 和第一轮细分计数复制也由真实资源访问与区段位置建立；光栅相关显式顺序边
数量为零。

## Geometry and shade regions

主视图几何工作区段依次包含根播种、第一轮持久队列遍历与剔除、第一阶段光栅、
第一阶段 HiZ、延迟恢复、第二轮持久队列遍历与剔除、第二阶段光栅、第二阶段 HiZ。
每轮使用一个 `CullClusterWork` pass，node 与延期 cluster 工作在 GPU 队列中连续消费，
叶节点生成的候选在当前 wave 内直接完成共同的 cluster 剔除。累计候选指标与延期队列数量分别持有。
目标视图两轮工作分别位于中间遮挡区段两侧，延迟恢复位于第二轮工作开头。
共享缓冲的资源读写与区段位置直接连接到各轮光栅；发布、计数与中断恢复规则见 [[Worklists]]。

着色区段依次包含分箱、按着色箱编号调和的材质区段映射与背景解析。材质箱保存 shader
绑定和常量输入；值比较覆盖代表材质、pipeline、命令资源和材质属性绑定。值变化时，
RenderGraph 删除旧子树并安装替代子树，保留箱的常量随箱数量变化更新。

背景解析填充空像素的颜色与运动矢量，几何像素保留材质箱已经写入的结果。两类像素
共同构成完整输出；解析的部分写入声明保留前面的箱写入，并声明最终输出内容已定义。
空场景关闭几何工作、目标几何和着色分箱及材质区段，目标清除与背景解析继续执行。
分箱 scatter 的区段启用还由紧凑像素存储的选择控制。每帧将所需启用状态交给
RenderGraph，由图比较当前状态并传播实际变化；状态的唯一持有者为区段树。取消帧
与结构版本切换之后，同样按当帧数据设置区段启用。

候选指标在第一阶段 HiZ 区段内复制到图持有的指标缓冲，随后候选缓冲供延迟恢复复用。
指标收集完成后，反馈区段中的持久回读交付页缺失与帧指标。
帧派发尺寸、资源绑定与完成交付使用的数据关联随当前图数据保存。
阶段顺序、遍历前后链与子树成员由 RenderGraph 的区段树维护，Cluster 显式顺序边数量为零。

Cluster 把 shader 解析与局部图修改放在同一编辑作用域，候选映射、资源描述及材质输入
仅在图提交成功后一起接替当前数据。编辑异常时丢弃作用域；下一次调和继续使用当前
已提交的数据。关闭管线时释放资源与订阅，并一次替换图数据集合。

## Frame resources

RenderGraph 持有材质标量、标量地址、光栅箱与着色箱索引的持久缓冲。材质业务保存
当前编码值；变化范围引用对应值并直接填入图上传映射跨度。新建或尺寸变化的缓冲写入
完整数据，其余修改只写变化范围。图编辑提交同时接替参数值、缓冲描述和待写范围；
取消帧保留业务变化，之后的帧继续写入。

主视图数组通过 `SetBufferData` 写入图管理的当帧上传页；目标视图通过 `Upload` 设置当帧输入。
scene/seed/shader 常量直接填入 `GetBufferDataWriteSpan` 返回的图映射跨度；参数绑定位置
随各 shader 输入保存。图帧资源持有实际映射缓冲和在途生命周期，参数的空闲位置供后续局部
编辑复用。场景分组索引的变化写入其图持久缓冲。

HiZ 使用两张图持久纹理。`GraphTextureInput` 在每帧选择之前的 HiZ 与当前写入纹理，
纹理端点状态和队列完成由图维护。Cluster 保存相机矩阵、视图身份及当前写入索引。
几何完成 pass 成功提交后接替矩阵并旋转索引；取消帧保留之前的矩阵与索引。

页缺失与帧指标使用反馈区段内的 `GraphReadbackId`，标签为驻留 epoch。帧指标配置启用时
安装指标收集与回读；`ClusterFrameMetrics.ResidencyEpoch` 提供实际交付标签。反馈到达和
消费时机由 [[architecture/rendering/Delayed-Feedback]] 描述。

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

`StageRegionRenderingTests` 使用相同硬件组合，逐字节比较 64×64 的公开颜色和深度输出。
自给的八面体页数据在公共 Mesh BVH 中追加四个父节点，运行中加入该网格后的第一帧
与全新安装逐字节一致。其余用例覆盖空场景后恢复、在恢复几何并新增材质的帧取消后于下一帧恢复输出、
着色材质新增与删除，以及包含
未解析外部着色函数、经 Slang 目标代码链接确认失败的材质在编辑异常后移除，随后
输出恢复为失败前的图像。测试通过公开 Mesh、ECS、管线、纹理复制与缓冲映射观察行为。

`FrameResourceRenderingTests` 使用 D3D12 与 Vulkan 硬件适配器，验证材质标量修改后的
下一帧颜色、深度与全新材质场景一致，以及帧指标的读取、epoch 与关闭配置下的公开 pass 列表。
`ClusterRenderingTests` 比较相机移动后的 84 帧颜色与每帧放弃相机历史的参考，并通过公开
反馈统计验证页缺失在之后的帧消费，随后网格具有驻留页和可见覆盖。

## Material inputs

Cluster 按 `(Material, 属性编号)` 持有一个 `GraphParameterInput`。使用该属性的 pass
绑定同一个输入，帧录制为每个输入设置一次值。参数绑定同时声明纹理和缓冲的真实访问；
纹理上传与最终状态转换通过输入连接到它的全部使用者，连接关系由 RenderGraph 持有。

材质箱的代表材质变化时，图编辑同时替换参数绑定和访问。细分组的共享 split pass
沿用该方式切换到当前种子材质。编辑完成后，Cluster 按图中的实际参数绑定保留材质输入。

## Relations
[[architecture/render-graph/Retained-Graph]] · [[architecture/render-graph/Frame-Data]] · [[architecture/render-graph/Extension-Points]] · [[Cluster-Scope]]
