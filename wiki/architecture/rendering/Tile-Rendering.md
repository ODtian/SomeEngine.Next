# Tile 渲染接入

Tile 通过持久 RenderGraph 接收几何与材质入口，沿用接入前的类型化材质、资源连接和阶段组织方式。

## 职责与数据

| 实现 | 持有数据 | 执行行为 |
| --- | --- | --- |
| `ClusterPipelineSystem` | Cluster 几何来源、材质分箱、用户材质属性与图输入的关联 | 选择类型化 shader 入口，连接几何和材质资源，根据实例、材质、相机和驻留变化判断几何更新 |
| `TileRendering` | 投影三角形、cell 引用、排序工作、覆盖记录、深度分区及各阶段 pass | 安装索引、覆盖、请求和合成链，声明自身资源访问，按构建 pass 的提交结果维护几何复用状态 |
| `TileShaders` | 已链接的 compute 和硬件光栅管线 | 通过已有 `LinkedComputePipeline` 和 Graphics 入口创建管线，随渲染系统关闭释放 |
| `RenderDevice` | GPU 资产分配、视图、采样器、描述符和上传 | 准备纹理与采样器，并按 GPU 完成点释放替换资源 |
| RenderGraph | 图资源、参数输入、访问依赖和提交完成点 | 执行资源同步、pass 次序与资源回收 |

`TileRenderingDescription` 提供视图与 cell 数量、容量、shader 布局步长和索引选项。布局步长从几何构建入口的已链接管线反射取得，对应当前编译目标。具体顶点与材质数据由用户 shader 解释。

## 图连接

Cluster 提供已有的 `GraphResourceBinder`，将几何、材质及 Feature 输入接到 Tile 创建的 pass。Tile 资源通过 `IGraphResourceProvider` 提供；资源名解析发生在图编辑期间，帧执行使用已经建立的图连接。

Tile 自己创建 compute、间接 dispatch 和硬件覆盖 pass。Cluster 的材质变化仍通过原有参数输入和标量数据更新流程送入这些 pass。

Count 与 Scatter 按实际图元或来源数量派发线程组。数量在一个 64 线程组内时，每组共享一份 cell 范围，由 64 个线程分摊 XYZ cell 和硬件关联；大量图元继续由每个线程处理一份范围，保持微三角形吞吐。两种粒度共用 cell 遍历与连续引用分配，GPU 计数形成对应的间接参数。

光栅、着色与透明入口通过 `Material.ResolveResourceProperties` 匹配属性编号，分箱根据关联结果汇总资源。缓冲视图和加速结构通过 `Material.GetDeviceResourceBinding` 形成借用的绑定值；Cluster 将绑定值写入 RenderGraph 参数输入。采样器通过 `RenderDevice.RetainSampler` 取得，描述符索引也持有同一份设备采样器；纹理继续通过 `PrepareTextureForInputs` 准备。完成与释放规则见 [[architecture/rendering/RenderDevice|RenderDevice]]。

稳定拓扑保存在持久图内。Cluster 根据几何版本、实例、相机和驻留状态提出复用判断；Tile 通过已有的 `IRenderGraphFrameExecutionObserver` 自行接收构建 pass 的提交结果与完成点，维护复用条件，并更新自身 pass 的启用状态。

`Install` 接收 Cluster 选定的颜色入口。材质状态晚加入 Ordered 时，Cluster 准备对应 shader，再调用 `SetColorPasses` 显式更新颜色 pass。Tile 保留索引资源和构建拓扑，新颜色链排在覆盖与 stencil 清空之后；最后一个颜色 pass 继续提供共享 stencil 的完成点。

## 材质求值

用户继续提供 `IVertexEvaluate`、`IDomainEvaluate`、`IVisibilityEvaluate`、`ISurfaceEvaluate` 与 `ITransparentSurfaceEvaluate`。

固定清空、来源复制、cell 构建与排序入口优先复用所选 Shader 已有的入口。动态材质组合只生成本次采用的几何、颜色入口与缺少的固定入口；颜色资源表表明使用 VSM 时，单独取得保存的请求特化或组合该请求入口。晚加入 Ordered 沿原有准备路径取得对应的两个颜色入口。

原有着色阶段与透明阶段共同调用 `ResolveBarycentricsAtDepth` 和 `EvaluateMaterialHit` 准备命中输入，再通过 `evaluateSurface` 或 `evaluateTransparency` 进入用户的表面求值。透明投影记录提供已经计算的投影系数；原有着色入口从图元计算投影系数。用户三角形与求值数据保留在当次 pass 的局部变量或组共享数据中，两条路径共用同一份重建与表面输入实现。

LocalTile 先从组共享来源或当次计算取得同一份 `Shading`，再通过一个 `shadeShading` 调用点进入完整用户材质。8 项命中队列通过后续批次完成累积，16 份组共享来源数据供本组复用。

阴影页请求保留在 VSM 页分配之前的请求阶段。普通请求从用户 Domain 取得位置，透明请求从当次覆盖深度还原位置，两者直接调用 VSM 的 `MarkVirtualShadowPages`。几何和引用完整驻留时，透明请求按既有 occupied XY tile 列表派发，每批 16 图元在组内复用当前求值数据；全扫描和预算继续处理沿来源投影枚举。材质的表面求值接口提供颜色与调试行为。

实现入口：`src/SomeEngine.Render/Tiles/`、`ClusterPipelineSystem.Transparency.cs`、`cluster_shade_pipeline.slang` 与 `cluster_transparency_material.slang`。

## 硬件覆盖

覆盖片元将自己的位写入同一份覆盖缓冲。SPIR-V 入口逐片元执行原子 OR；SM 6.5 入口通过 `WaveMatch` 合并同地址的写入。

Vulkan 的 helper 片元可以参加子组运算，其存储与原子操作按片元规则处理。逐片元写入使实际覆盖片元直接完成自己的写入，覆盖记录与材质求值各自执行。[子组规则](https://docs.vulkan.org/glslext/latest/glslext/khr/GL_KHR_shader_subgroup.html#_modifications_to_the_opengl_shading_language_specification_version_4_50)、[片元执行规则](https://docs.vulkan.org/spec/latest/chapters/shaders.html#shaders-helper-invocations)。
