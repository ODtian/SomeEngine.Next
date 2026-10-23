# GPU 地形几何

本页维护已实现的公共接口、数据职责与调用方法。源码入口为 [TerrainClusterAdapter.Gpu](../../../src/SomeEngine.Terrain/TerrainClusterAdapter.Gpu.cs)、[TerrainGpuSurfaceData](../../../src/SomeEngine.Terrain/TerrainGpuSurfaceData.cs)、[TerrainGpuMesher](../../../src/SomeEngine.Terrain/Meshing/TerrainGpuMesher.cs)、[RenderTerrainGeometry](../../../src/SomeEngine.Terrain/RenderTerrainGeometry.cs) 和 [terrain_surface_cells.slang](../../../assets/Shaders/terrain_surface_cells.slang)。字段编辑见 [GPU 字段编辑](Terrain-Gpu-Editing.md)，空间块见 [地形放置与分块](Terrain-Scene.md)，原生构建见 [GPU 几何构建](../rendering/Geometry-Features.md#gpu-几何构建)。

`TerrainScene(TerrainClusterAdapter, TerrainChunkGrid)` 使用 GPU 几何适配器。它与 CPU 构造入口共用放置、共享几何编号、受影响块集合和每个 Terrain 的完整构建批次。`TerrainGeometryFeature` 安装同一组游戏系统和地形请求提取，并增加 GPU 几何的准备、帧录制与完成观察。

`TerrainClusterAdapter` 接收 `ITerrainMesherGpu`、`TerrainGpuAttributes` 和 `ClusterGpuVertexEncoder`。`MarchingCubesTerrainMesherGpu` 接收表面 Shader 及计算入口，消费捕获的标量源，使用与 CPU 提取器相同的 Transvoxel 普通单元表和过渡单元表。默认入口为 `ExtractHeightSurface`；体素 Shader 使用 `ExtractVoxelSurface`，混合表面 Shader 使用 `ExtractBlendSurface`。三个入口复用同一套普通单元与六面过渡循环，输出位置、索引、顶点与索引计数，以及具有来源位置和法线的采样点。

| 标量源 | 地形局部采样 | 标量含义 | 单元范围 |
| --- | --- | --- | --- |
| `HeightSurface` | X/Z 米制位置的双线性插值 | `y-H(x,z)` | 按每列高度范围定位竖向单元 |
| `VoxelSurface` | X/Y/Z 米制位置的三线性插值 | `D(x,y,z)` | 当前空间块的三维单元 |
| `BlendSurface` | 各输入节点的地形局部米制位置 | `(1-w) Fa + w Fb`，权重遵循[表面组合](Terrain-Surfaces.md) | 当前空间块的三维单元 |

体素源按空间块和算法读取邻域准备稀疏页表。负坐标采用向下取整的页划分，页表与页内样本均以 X 变化最快，其后为 Y、Z；缺省页使用字段的 `InitialValue`。高度和体素的固定源实现分别位于 [terrain_height_input.slang](../../../assets/Shaders/terrain_height_input.slang) 与 [terrain_voxel_input.slang](../../../assets/Shaders/terrain_voxel_input.slang)。

混合图捕获所有节点的父关系、输入字段引用、常量权重、区域和渐变距离；相同字段共享一份固定输入。源描述保存各字段的网格、页表和样本起点，供单源与组合求值共用。录制时合并各字段的设备样本和页表，`terrain_blend_input.slang` 按叶节点到根的权重乘积累加标量；节点数和字段数来自本次捕获。嵌套图中的每个字段继续使用自己的采样间距和默认值，零等值面与负值实心约定沿用 CPU 表面。

`ITerrainMesherGpu.GetCapacity` 给出预留缓冲所需的顶点和三角形数量上限，`Record` 通过 `TerrainGpuMesherContext.AddCompute` 录制提取入口。普通单元的上限为 12 个顶点和 5 个三角形，每个启用过渡面的单元上限为 12 个顶点和 12 个三角形。实际计数由 GPU 写出，并限定后续属性和 Cluster 构建读取的有效前缀。

GPU 提取参数保存当前单元间距、最细格距和六面 `TransitionFaces`。普通边与过渡边共用最细格距交点细化和法线求值，普通粗块顶点与过渡粗侧顶点共用切平面位移；细侧交点保持原始位置。坐标、case 编号、边界位移和面朝向的规则统一由[地形 LOD](Terrain-Lod.md)维护，GPU 对应实现位于 [terrain_surface_cells.slang](../../../assets/Shaders/terrain_surface_cells.slang) 和 [terrain_surface_transitions.slang](../../../assets/Shaders/terrain_surface_transitions.slang)。

`TerrainGpuAttributes` 指定属性 Shader、入口、每顶点字节数、额外读取邻域与 `TerrainFieldInputs Fields`。属性入口以每组 64 个线程运行，读取 `TerrainSamples` 和 `TerrainCounts`，写入 `TerrainAttributes`。每个采样点依次存储 `float3` 来源位置与 `float3` 法线；位置和索引分别使用 `float3` 与 `uint`。属性字节由配套 `ClusterGpuVertexEncoder` 和材质 `IVertexEvaluate` 解释。

`Fields.HeightFieldIds` 和 `Fields.VoxelFieldIds` 与表面源共同捕获，同一字段在本批次共享一份固定输入。GPU 属性入口通过 `TerrainAttributeHeightSources` 和 `TerrainAttributeVoxelSources` 两个 `StructuredBuffer<int>` 取得源索引；每个数组的顺序与对应字段声明顺序一致，数组元素用于索引 `TerrainSources`。声明的字段变化进入 TerrainScene 的受影响块选择。CPU 属性的同批次读取见[表面组合](Terrain-Surfaces.md)。

`MaterialEntryPoint` 指定同一 Shader 中可选的逐三角形材质入口，以每组 64 个线程运行，写入 `TerrainTriangleMaterials` 中的 `uint` 编号；有效三角形数量为 `TerrainCounts` 的索引数除以 3。`MaterialCount` 给出编号范围 `[0, MaterialCount)`，传入通用 GPU 构建器的预留容量。单材质调用通过 `MaterialIndex` 指定整批的材质编号。字段绑定、用户编码与细分示例见[地形材质](Terrain-Materials.md)。

批次提交时，在 Terrain 的数据锁内固定字段页、源版本和所需范围；`PrepareGpuRead` 为选中的文件页取得 CPU 数据，并沿用设备页的完成依赖。几何请求与字段请求一起保留到图提交确认，并由现有 `TerrainRenderExtractor` 传到 Rendering world。`RenderTerrainData` 继续持有设备字段页和页表。`RenderTerrainGeometry` 在捕获输入就绪后预留原生范围，并在同帧图中依次录制表面提取、用户属性和通用 Cluster 构建。原生页堆与 BVH 由 Cluster 驻留系统持有。

完成状态分别通过以下公开入口观察：

| 阶段 | 版本与完成入口 | 完成含义 |
| --- | --- | --- |
| 字段编辑 | `TerrainEdit.Revision`、`Completion`、`WaitAsync` | 对应字段页的写入完成 |
| GPU 几何 | `TerrainScene.GetGpuBuild(terrain).Revision`、`Completion` | 已提交批次的表面、属性、Cluster 页面和 BVH 完成，或提交前的构建请求已撤销 |
| 可见绑定 | 同一 `TerrainGpuBuild.Visibility` | 渲染世界已应用本批次的当前选择；移除全部放置后的空选择也完成此句柄 |

`TerrainScene.WaitForBuildsAsync` 等待已提交的几何工作。游戏侧 `TerrainCommitSystem` 在整批构建完成后一起推进对应 `TerrainChunk.Revision`；`GetChunks` 读取这些提交版本。随后提取器传递块版本、变换、材质和场景当前及构建中的几何选择，渲染准备按版本绑定共享 `ClusterGeometry`。生成期间保留当前画面；新批次绑定后或放置移除后，通过 `ClusterGpuBuilder.Release` 归还退出选择的范围，实际复用由最后使用完成点决定。当前选择仍包含的 LOD 新结果保持到首次绑定；已退出选择的结果按照相同完成点回收。

应用停止先终结游戏侧最新批次和渲染侧已接收批次的选择状态，再排空系统 Job。已提交构建继续等待 GPU 完成点，提交前撤销的构建释放固定输入。字段写入与读回的停止结果见 [GPU 字段编辑](Terrain-Gpu-Editing.md)。

GPU 构建完成点释放请求持有的字段输入引用，已完成记录保留批次版本、块范围及完成状态。设备页继续由 `RenderTerrainData` 根据来源引用与图完成点回收。

[GpuHeightTerrainExample](../../../samples/DefaultGame/GpuHeightTerrainExample.cs) 是可编译示例。四米方形、每米一个采样点、两层竖向块、两个放置及 `float3` 位置、`float3` 法线、`float2` UV 编码都属于示例选择：

```csharp
using var scheduler = new Scheduler();
using var ground = await GpuHeightTerrainExample.CreateAsync(scheduler, contentDirectory);
ground.Install(application);
application.UpdateFrame = frame =>
{
    if (frame.Index == 0) ground.AddPlacements(firstMaterial, secondMaterial);
    if (frame.Index == 60) ground.AddHeight(1f);
};
await Application.RunAsync(args, application: application);
```

CPU 查询通过显式 `HeightField.ReadCpuAsync(range)` 请求所需样本。公开应用行为入口为 [TerrainGpuGeometryTests](../../../tests/SomeEngine.Runtime.Tests/TerrainGpuGeometryTests.cs)，对照相同字段、笔刷、相机和材质的 CPU/GPU 画面，并观察批次完成、可见绑定与块版本。

[GpuVoxelTerrainExample](../../../samples/DefaultGame/GpuVoxelTerrainExample.cs) 展示稀疏体素源：`Install` 安装同一批次流程，`AddPlacement` 放置采样球体，`AddSphereDelta` 在 GPU 修改标量后生成新的完整几何。四单元块、每米一个样本、初始单位球及 `[-3,4)` 的初始区域属于示例参数，位置/法线/UV 使用与高度示例相同的用户编码。`VoxelField.ReadCpuAsync(range)` 提供显式查询；[TerrainGpuVoxelGeometryTests](../../../tests/SomeEngine.Runtime.Tests/TerrainGpuVoxelGeometryTests.cs) 通过相同 CPU/GPU 操作的画面、字段版本与整批几何完成状态观察该流程。

[GpuBlendTerrainExample](../../../samples/DefaultGame/GpuBlendTerrainExample.cs) 从高度表面初始化局部体素，再通过 `AddHeight` 与 `AddSphereDelta` 编辑两个输入。八米地表、一米采样间距和两米渐变距离属于示例参数。公开应用检查 [TerrainGpuBlendGeometryTests](../../../tests/SomeEngine.Runtime.Tests/TerrainGpuBlendGeometryTests.cs) 将另一份不同采样间距的高度场加入嵌套组合，按相同参数编辑 CPU/GPU 源，比较固定版本表面求值、初始画面及两次编辑后的画面。

[GpuLodTerrainExample](../../../samples/DefaultGame/GpuLodTerrainExample.cs) 与 CPU `LodTerrainExample` 使用相同的混合源、根范围、距离数组和局部球形笔刷。32×16 米范围、每边四个单元和 5/9 米细分距离属于示例参数。[TerrainGpuLodGeometryTests](../../../tests/SomeEngine.Runtime.Tests/TerrainGpuLodGeometryTests.cs) 沿相同相机移动与编辑序列比较粗细接缝画面和最终画面，并观察块层级、整批版本与设备完成、可见绑定完成状态。
