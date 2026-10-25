# GPU 字段编辑与 CPU 读取

本页维护已实现的公共 API、数据职责与调用方法。源码入口为 [HeightGpuEditing](../../../src/SomeEngine.Render/Terrain/HeightGpuEditing.cs)、[VoxelGpuEditing](../../../src/SomeEngine.Render/Terrain/VoxelGpuEditing.cs)、[TerrainFieldPage](../../../src/SomeEngine.Resources/Terrain/TerrainFieldPage.cs)、[TerrainRenderExtractor](../../../src/SomeEngine.Render/Terrain/TerrainRenderExtractor.cs) 和 [RenderTerrainData](../../../src/SomeEngine.Render/Terrain/RenderTerrainData.cs)。CPU 字段见 [Terrain-Fields](Terrain-Fields.md)，放置与几何生成见 [Terrain-Scene](Terrain-Scene.md)。

## 提交与完成

`HeightField.EditGpu<TBrush>` 接受实现 `IHeightBrushGpu` 的值类型笔刷；`VoxelField.EditGpu<TBrush>` 接受实现 `IVoxelBrushGpu` 的值类型笔刷。两个入口都接受可选 `JobHandle` 依赖。提交复制笔刷值、捕获源字段版本并为写入区域建立新页，返回新版本的 `TerrainEdit`。前一次字段编辑与调用方依赖共同决定 GPU 工作的输入就绪时间。

`HeightDeltaBrushGpu` 接受已驻留的 `Shader`、样本区域和高度增量，调用 [terrain_height_delta.slang](../../../assets/Shaders/terrain_height_delta.slang) 的 `AddHeightDelta` 入口。对应的 [Shader 定义](../../../assets/Shaders/terrain_height_delta.shader) 可通过现有 Shader 资产读取与编译路径取得。

`VoxelSphereDeltaBrushGpu` 接受已驻留的 `Shader`、`VoxelGrid`、球心、半径和标量增量，调用 [terrain_voxel_delta.slang](../../../assets/Shaders/terrain_voxel_delta.slang) 的 `AddVoxelSphereDelta` 入口。球心和半径使用地形局部米制坐标，增量从球心到半径边界线性衰减。对应的 [Shader 定义](../../../assets/Shaders/terrain_voxel_delta.shader) 使用相同资产读取与编译路径。

`Terrain` 持有请求序号和待提取的请求。`TerrainSource` 组件将地形请求纳入 Game world 到 Rendering world 的既有提取；同一 Terrain 的多个实体共用一份捕获请求。一次应用运行的多个 `TerrainGeometryFeature` 共用同一 `TerrainRenderExtractor` 和 `RenderTerrainData`，设备字段页、准备、帧录制和完成观察由这份数据统一持有，各 Scene 保留各自的提交与 LOD 游戏系统，后台几何批次在游戏计算完成后、提取前由帧协调者提交。Scene 继续持有跨帧构建完成；地形 GPU 页请求随后经同一提取入口提交。

`RenderTerrainData` 持有设备页和管线。它按请求需要上传 CPU 页，建立源页表，并在持久图的扩展点录制当帧复制和 compute 工作。每次编辑使用捕获的输入页，写入新输出页。

RenderGraph 提交回执使 Terrain 释放对应的待提取请求。实际队列完成经后端等待确认后，通过共同 Task fence 进入请求的终态 Job，再完成 `TerrainEdit.Completion`。`TerrainEdit.WaitAsync` 因此等待本次 GPU 结果就绪。

应用停止时先等待已提交 GPU 使用，随后由 Terrain 结束尚待图提交的字段请求，并在排空游戏系统 Job 前处理对应 CPU 网格依赖。撤销的字段编辑与页读回返回 `OperationCanceledException`；已提交字段工作保持真实队列完成结果。实现入口为 [TerrainDataRequest](../../../src/SomeEngine.Resources/Terrain/TerrainDataRequest.cs)、[TerrainScene](../../../src/SomeEngine.Terrain/TerrainScene.cs) 和 [ApplicationGeometry](../../../src/SomeEngine.Runtime/ApplicationGeometry.cs)。

`RenderTerrainData` 释放设备页前，通过 RenderGraph 复制仍存活 GPU 版本的内容，等待对应队列完成点，再保存到原 `TerrainFieldPage` 的 CPU 副本。字段页分别记录读回目标数组和已经填充的 CPU 内容；后续安装的设备缓冲从完成的内容上传。`ApplicationScene.Reload` 后，调用方保留的 Terrain 继续提供相同版本的样本和几何输入。已撤销读回任务保留调用时的取消结果，此后新发起的读取取得保存后的内容。对应实现为 [TerrainFieldPage](../../../src/SomeEngine.Resources/Terrain/TerrainFieldPage.cs) 和 [RenderTerrainData](../../../src/SomeEngine.Render/Terrain/RenderTerrainData.cs)。

## 局部读取与后续 CPU 工作

`HeightField.ReadCpuAsync(range)` 与 `VoxelField.ReadCpuAsync(range)` 在调用时捕获字段版本，并为所需 GPU 页请求 CPU 物化。GPU 就绪信号与 CPU 数据完成分别由共同 fence 和请求的最终 Job 表达。最终 Job 在样本数组写访问中映射读回缓冲、复制样本并标记页内容，随后清理缓冲；GPU 失败和复制失败也经过同一终态清理。公开 CPU 读取完成点覆盖复制、映射释放和缓冲清理，`RenderTerrainData` 的关闭等待跟随该最终完成点。读回保存该页的原版本；同一页版本的后续读取共用 CPU 数组及其完成依赖。返回的 `HeightFieldRead` 或 `VoxelFieldRead` 继续保持调用时的样本版本。

GPU 请求由安装了地形功能的应用帧处理。CPU 查询、后续 CPU 笔刷和 CPU 网格任务通过相同的捕获入口取得所需页，并等待对应完成依赖。`HeightSurface` 和 `VoxelSurface` 的 `ReadCpuAsync`、`ScheduleRead` 共用这条数据就绪路径。

GPU 高度编辑产生 `HeightFieldChange`，GPU 体素编辑产生 `VoxelFieldChange`。游戏侧提交系统按范围选择受影响块，网格系统等待 CPU 样本后构建几何，完成的共享几何再进入既有 Mesh 提取和 Cluster 路径。

## 邻域与多阶段计算

[HeightNeighborhoodBrushesGpu](../../../src/SomeEngine.Render/Terrain/HeightNeighborhoodBrushesGpu.cs) 和 [VoxelNeighborhoodBrushesGpu](../../../src/SomeEngine.Render/Terrain/VoxelNeighborhoodBrushesGpu.cs) 复用 [CPU 邻域笔刷](Terrain-Brushes.md) 的参数对象。每个 GPU 笔刷接受 `Shader` 加对应 CPU 笔刷，也提供 `Shader` 加网格、中心、半径及操作参数的构造方法。

| 操作 | 高度场 | 体素场 |
| --- | --- | --- |
| 平滑参数 | `HeightSmoothBrush` | `VoxelSmoothBrush` |
| 平滑 GPU 笔刷 | `HeightSmoothBrushGpu` | `VoxelSmoothBrushGpu` |
| 平滑 compute 阶段 | `AverageHeight` → `SmoothHeight` | `AverageVoxel` → `SmoothVoxel` |
| 压平参数 | `HeightFlattenBrush` | `VoxelFlattenBrush` |
| 压平 GPU 笔刷 | `HeightFlattenBrushGpu` | `VoxelFlattenBrushGpu` |
| 压平 compute 阶段 | `FlattenHeight` | `FlattenVoxel` |
| Shader 源码 | [terrain_height_neighborhood.slang](../../../assets/Shaders/terrain_height_neighborhood.slang) | [terrain_voxel_neighborhood.slang](../../../assets/Shaders/terrain_voxel_neighborhood.slang) |

平滑先从固定输入计算邻域均值，再将源值和均值按径向权重混合到目标页。平均值缓冲区属于当前帧，第二阶段声明对它的读取依赖。压平直接从固定输入和目标平面写出目标页。两者的 `Range`、`ReadBorder`、米制坐标与线性衰减均取自对应 CPU 参数对象。

两个 GPU context 都通过 `SourceSamples`、`SourcePageOffsets`、`Output` 提供输入样本、页映射和目标页，通过 `Graph` 提供当前帧的资源创建能力。自定义笔刷使用带 `bindings` 的 `AddCompute` 重载，为各阶段显式声明 Shader 资源及访问方式；这些绑定依次位于参数常量之后。每次调用自动排在前一阶段之后，最终的 `TerrainEdit` 完成依赖覆盖全部阶段和输出页。[TerrainBrushCompute](../../../src/SomeEngine.Render/Terrain/TerrainBrushCompute.cs) 统一录制这组计算，内置平滑笔刷给出了完整的两阶段实现。

## 可编译示例

[HeightTerrainExample](../../../samples/DefaultGame/HeightTerrainExample.cs) 的 `AddHeightGpu` 演示同一高度场在两处放置中的 GPU 修改。下面的第 60 帧和一米增量属于示例参数；`heightBrush` 是应用预先取得的驻留 Shader。

```csharp
using var scheduler = new Scheduler();
var ground = new HeightTerrainExample(scheduler);
ground.Install(application);
application.UpdateFrame = frame =>
{
    if (frame.Index == 0)
        ground.AddPlacements(firstMaterial, secondMaterial);
    if (frame.Index == 60)
        ground.AddHeightGpu(heightBrush, 1f);
};
await Application.RunAsync(args, application: application);
```

[VoxelTerrainExample](../../../samples/DefaultGame/VoxelTerrainExample.cs) 的 `AddSphereDeltaGpu(shader, center, radius, delta)` 演示三维场的 GPU 修改。它把球形增量写入 `Field`，再通过 `VoxelFieldChange` 更新受影响空间块；CPU 侧的 `VoxelSphereDeltaBrush` 可使用相同球心、半径和增量。

两个示例还提供 `SmoothGpu(shader, center, radius, neighborRadius, strength)` 和 `FlattenGpu(shader, center, radius, plane, strength)`，分别使用对应的 `terrain_height_neighborhood` 或 `terrain_voxel_neighborhood` Shader。下面的中心、半径、邻域与强度属于示例参数；CPU 与 GPU 直接共享同一个参数值。

```csharp
var brush = new HeightSmoothBrush(ground.Heights.Grid, new Vector2(2, 2), 3f, 1, .5f);
TerrainEdit edit = ground.Heights.EditGpu(new HeightSmoothBrushGpu(heightNeighborhoodShader, brush));
```

自定义 GPU 高度笔刷通过 `HeightBrushGpuContext` 取得网格、写入区域、当前输出页及源页表范围。`AddCompute` 绑定参数、源样本、页偏移和输出页，入口及参数含义由调用方的 Shader 定义。

自定义 GPU 体素笔刷通过 `VoxelBrushGpuContext` 取得相同职责的三维数据。源页表按 X、Y、Z 顺序排列，X 变化最快；`SourcePageX/Y/Z` 为最小页坐标，`SourcePageWidth/Height` 为二维层内尺寸。每项记录源样本缓冲区中的元素偏移，值为 `-1` 时读取字段的 `InitialValue`。页内样本同样按 X、Y、Z 顺序排列。`Page` 表示完整输出页，笔刷写出完整页并保留 `Range` 外的原值。

公开行为入口为 [TerrainGpuEditingTests](../../../tests/SomeEngine.Runtime.Tests/TerrainGpuEditingTests.cs)：同参数 CPU/GPU 采样、混合编辑的版本顺序，以及字段修改后的几何画面。[TerrainGpuBrushRenderingTests](../../../tests/SomeEngine.Runtime.Tests/TerrainGpuBrushRenderingTests.cs) 对照平滑和压平后的 CPU/GPU 表面。

跨页的区域级距离求解、精度报告与算法替换见[体素距离重建](Terrain-Distance.md)。它复用本页的固定源、页持有与实际完成路径。
