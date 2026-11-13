# Cluster Scope

## Definition
Cluster 是 triangle virtual geometry 与 programmable raster 的可替换 pipeline template，不是引擎默认 renderer，也不是唯一 renderer。

## Includes
- triangle virtual geometry representation
- LOD/BVH traversal
- geometry page streaming
- stage grouping
- Deform cache
- software/hardware raster
- tessellation/dicing integration

## Pipeline responsibilities

`ClusterGeometryPipeline` 收集 Rendering world 的视图，更新剔除、BVH 遍历、光栅、可见性和
HiZ 的帧输入。`ClusterShadingPipeline` 持有材质阶段、标量和纹理输入，通过
`IRenderMaterialResources` 提供材质、资源绑定和已有着色常量的中性投影。
`ClusterGraphInstaller` 持有已安装的图资源、阶段映射与参数切片，负责首次安装和结构编辑。
三个类型各自位于一个文件，并通过显式引用协作。

材质表通过 `TakeChanges()` 取得一份完整的 `ClusterMaterialChange`：同一着色管线在返回前
解析变化的阶段、箱和标量，再由材质表消费其源变更。安装器通过 `Apply` 使用这份描述。
变化描述携带光栅箱、着色箱、标量范围、纹理和着色路径变化；阶段缓存计算变化，安装器
提交对应图编辑并持有已安装阶段。材质地址与箱的映射继续增量维护。
安装器分别比较材质地址容量与光栅箱数量，容量变化时更新现有分桶参数；复用已有光栅箱的
新增材质地址也会随本次编辑进入分桶读取范围。

`RenderInstanceChangeData` 从同一 Rendering world 组件和实例地址读取变化、缓存选择与
LOD 范围，供几何和 VSM 消费。`ShadowCasterCache` 持有 VSM 的缓存状态。
混合阶段计时与工作量读取通过工具程序集的 `BlendMeasurementFeature` 安装。

## Excludes
Lighting、shadow/VSM、透明投影与合成、材质贴花、motion、streaming feedback consumer、temporal 与 presentation 位于各自 feature。Cluster 通过 Render 的中性几何与材质资源投影提供输入。透明与贴花拥有自己的 tile 网格、图资源和提交后复用记录，见 [[architecture/features/Translucency]]。ray tracing bridge 与 debug 的历史实现边界见 [[history/Rendering/Removed-Code-2026-10-04|删除记录]]。

## Relations
[[architecture/features/Virtual-Shadow-Maps]] · [[architecture/features/Lighting]] · [[Raster-Execution]]
