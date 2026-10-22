# GPU 字段编辑与 CPU 读取

本页维护已实现的公共 API、数据职责与调用方法。源码入口为 [HeightGpuEditing](../../../src/SomeEngine.Render/Terrain/HeightGpuEditing.cs)、[VoxelGpuEditing](../../../src/SomeEngine.Render/Terrain/VoxelGpuEditing.cs)、[TerrainFieldPage](../../../src/SomeEngine.Resources/Terrain/TerrainFieldPage.cs)、[TerrainRenderExtractor](../../../src/SomeEngine.Render/Terrain/TerrainRenderExtractor.cs) 和 [RenderTerrainData](../../../src/SomeEngine.Render/Terrain/RenderTerrainData.cs)。CPU 字段见 [Terrain-Fields](Terrain-Fields.md)，放置与几何生成见 [Terrain-Scene](Terrain-Scene.md)。

## 提交与完成

`HeightField.EditGpu<TBrush>` 接受实现 `IHeightBrushGpu` 的值类型笔刷；`VoxelField.EditGpu<TBrush>` 接受实现 `IVoxelBrushGpu` 的值类型笔刷。两个入口都接受可选 `JobHandle` 依赖。提交复制笔刷值、捕获源字段版本并为写入区域建立新页，返回新版本的 `TerrainEdit`。前一次字段编辑与调用方依赖共同决定 GPU 工作的输入就绪时间。

`HeightDeltaBrushGpu` 接受已驻留的 `Shader`、样本区域和高度增量，调用 [terrain_height_delta.slang](../../../assets/Shaders/terrain_height_delta.slang) 的 `AddHeightDelta` 入口。对应的 [Shader 定义](../../../assets/Shaders/terrain_height_delta.shader) 可通过现有 Shader 资产读取与编译路径取得。

`VoxelSphereDeltaBrushGpu` 接受已驻留的 `Shader`、`VoxelGrid`、球心、半径和标量增量，调用 [terrain_voxel_delta.slang](../../../assets/Shaders/terrain_voxel_delta.slang) 的 `AddVoxelSphereDelta` 入口。球心和半径使用地形局部米制坐标，增量从球心到半径边界线性衰减。对应的 [Shader 定义](../../../assets/Shaders/terrain_voxel_delta.shader) 使用相同资产读取与编译路径。

`Terrain` 持有请求序号和待提取的请求。`TerrainSource` 组件将地形请求纳入 Game world 到 Rendering world 的既有提取；同一 Terrain 的多个实体共用一份捕获请求。`TerrainGeometryFeature` 安装提取器、准备系统、帧系统和完成观察器，并保留原有三个游戏系统的执行顺序。

`RenderTerrainData` 持有设备页和管线。它按请求需要上传 CPU 页，建立源页表，并在持久图的扩展点录制当帧复制和 compute 工作。每次编辑使用捕获的输入页，写入新输出页。

RenderGraph 提交回执使 Terrain 释放对应的待提取请求。实际队列完成经后端等待确认后，才通过现有外部 fence 完成 `TerrainEdit.Completion`。`TerrainEdit.WaitAsync` 因此等待本次 GPU 结果就绪。

## 局部读取与后续 CPU 工作

`HeightField.ReadCpuAsync(range)` 与 `VoxelField.ReadCpuAsync(range)` 在调用时捕获字段版本，并为所需 GPU 页请求 CPU 物化。读回保存该页的原版本；同一页版本的后续读取共用 CPU 数组及其完成依赖。返回的 `HeightFieldRead` 或 `VoxelFieldRead` 继续保持调用时的样本版本。

GPU 请求由安装了地形功能的应用帧处理。CPU 查询、后续 CPU 笔刷和 CPU 网格任务通过相同的捕获入口取得所需页，并等待对应完成依赖。`HeightSurface` 和 `VoxelSurface` 的 `ReadCpuAsync`、`ScheduleRead` 共用这条数据就绪路径。

GPU 高度编辑产生 `HeightFieldChange`，GPU 体素编辑产生 `VoxelFieldChange`。游戏侧提交系统按范围选择受影响块，网格系统等待 CPU 样本后构建几何，完成的共享几何再进入既有 Mesh 提取和 Cluster 路径。

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

自定义 GPU 高度笔刷通过 `HeightBrushGpuContext` 取得网格、写入区域、当前输出页及源页表范围。`AddCompute` 绑定参数、源样本、页偏移和输出页，入口及参数含义由调用方的 Shader 定义。

自定义 GPU 体素笔刷通过 `VoxelBrushGpuContext` 取得相同职责的三维数据。源页表按 X、Y、Z 顺序排列，X 变化最快；`SourcePageX/Y/Z` 为最小页坐标，`SourcePageWidth/Height` 为二维层内尺寸。每项记录源样本缓冲区中的元素偏移，值为 `-1` 时读取字段的 `InitialValue`。页内样本同样按 X、Y、Z 顺序排列。`Page` 表示完整输出页，笔刷写出完整页并保留 `Range` 外的原值。

公开行为入口为 [TerrainGpuEditingTests](../../../tests/SomeEngine.Runtime.Tests/TerrainGpuEditingTests.cs)：同参数 CPU/GPU 采样、混合编辑的版本顺序，以及字段修改后的几何画面。
