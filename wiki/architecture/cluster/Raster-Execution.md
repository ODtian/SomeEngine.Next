# Raster Execution

## Definition
Indirect draw/dispatch 只是 Cluster triangle raster pipeline 当前内部命令机制，不是 Cluster 对外业务语义。

## Invariants
- Cluster 产生 bounded work sets 并按 stage compatibility grouping。
- Cluster 自己拥有 SW/HW raster command recording；调用方不安装 raster command callback，也不选择如何发 draw/dispatch。
- 当前内部可以使用 indirect draw/dispatch；将来具体 pipeline/backend 可以改用 mesh shader、work graph 或其它机制，而不改变 Cluster 对外 geometry/material 语义。
- 不公开只为替换底层命令方式而存在的 raster executor 对象。

基础功能提供常规 VS/PS 与 compute 路径，支持多材质、不透明和细分；mesh shader 作为可选的光栅实现。可见性写出在构建管线时选择：A64 路径把深度和原始三角形身份一起原子提交；缺少 A64 时提供全硬件光栅，以及深度和身份分别直接写入两条路径。调用方选择后者时，接受两项数据来自不同片元的情况。

当前 `ClusterVisibilityMode` 包含 `ImageAtomic64`、`BufferAtomic64`、`HardwareOnly` 和 `NonAtomic`。构造 `ClusterGeometryPipeline` 时，`ClusterPipelineOptions.VisibilityMode` 可显式指定可见性管线；`ForceHardwareRaster` 优先选择全硬件。省略显式模式时，依次选择设备支持的图像 A64、缓冲 A64 和全硬件。选中的模式贯穿软件、硬件像素、meshlet 和细分 dice 的烘焙入口。

`NonAtomic` 复用每像素 8 字节的可见性缓冲，以两个独立的 32 位写入分别保存原始三角形身份和 `asuint(depth)`，保留完整浮点深度。片元读取当前深度后进行普通比较，再直接写深度与身份；调用方接受并发写入造成的配对差异。缓冲 clear/resolve 角色分别选择现成 shader 资产中的 `ClearVisibilityNonAtomic` 和 `PSResolveVisibilityNonAtomic`：clear 写空身份与深度 1，resolve 将身份写入 `R32UInt` 可见性目标，将原始浮点深度写入 `D32Float` 深度目标。RenderGraph 按普通读写依赖组织这些阶段。

不透明着色的 `VisibilityBufferInput`、Forward 着色和不透明 VSM 页请求读取这两个解析后的目标。着色从身份恢复原始三角形，将深度反投影得到的位置交给用户 `Domain.reconstruct`；层片元着色沿自身记录保存的完整深度走相同反算接口。

## Relations
[[Hardware-Raster]] · [[Software-Raster]] · [[architecture/material/Raster-Compatibility]]

目标页硬件光栅先根据当前窗口、页请求、页表和接收区域解析 atlas 像素地址，
通过筛选的像素再调用用户的可见性 shader，并将其深度原子合并到目标像素。
地址解析和写入共用原页表及 atlas，用户 shader 继续解释自己的 varying 和深度。

目标页 mesh 光栅将用户 shader 输出的位置投影到当前光栅窗口。整个三角形的
保守边界位于实际请求页窗口之外时，输出退化三角形，使光栅器提前丢弃。
跨越投影 w 边界的三角形沿原路径处理。位置暂存属于当前 mesh 工作组。

目标页 mesh 光栅同时复用工作组页缓存，将三角形保守像素边界与请求页的接收区域
遮罩相交。边界向相邻像素扩展，覆盖光栅量化误差；最终像素继续检查请求和接收
区域。页缓存按工作组线程索引分配写入项，只初始化当前窗口包含的页，读写共用
原页表、代数和遮罩。超过缓存尺寸的窗口沿逐像素页解析路径处理。
