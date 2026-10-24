# 地形材质、属性与细分

本页维护已实现的用户属性接口、材质绑定和示例调用。源码入口为 [TerrainClusterAdapter](../../../src/SomeEngine.Terrain/TerrainClusterAdapter.cs)、[TerrainClusterAdapter.Gpu](../../../src/SomeEngine.Terrain/TerrainClusterAdapter.Gpu.cs)、[TerrainMaterialBlock](../../../src/SomeEngine.Assets/Schema/TerrainMaterialBlock.cs) 和 [TerrainMaterialExample](../../../samples/DefaultGame/TerrainMaterialExample.cs)。字段固定读取由[表面组合](Terrain-Surfaces.md)维护，GPU 输入绑定由 [GPU 地形几何](Terrain-Gpu-Geometry.md)维护。

## 材料字段与实例绑定

`TerrainMaterialBlock.Materials` 保存材质资产引用。`HeightSources` 与 `VoxelSources` 分别保存 `MaterialIndex`、用户输入名称 `Input` 和字段编号 `FieldId`，随 `TerrainAsset` 一起保存。重开后，应用按输入名称把这些字段编号交给自己的属性生成器，并把加载的普通 Material 数组交给 `TerrainPlacement`。资产读取入口与文件页生命周期见[地形资产](Terrain-Assets.md)。

属性生成器的 `TerrainFieldInputs` 声明它使用的高度与体素字段。TerrainScene 在同一批次捕获表面和这些字段，并把声明字段的变化纳入受影响块选择。`ReadBorder` 给出额外样本邻域；属性工作读取已捕获的版本。

| 阶段 | CPU 接口 | GPU 接口 |
| --- | --- | --- |
| 字段声明 | `ITerrainAttributeGenerator<T>.Fields` | `TerrainGpuAttributes.Fields` |
| 顶点属性 | `CreateVertex(surface, point)` 返回用户属性 | 属性入口按 `TerrainSamples` 写入 `TerrainAttributes` |
| 三角形材质 | `GetMaterial(surface, position)` 返回材质编号 | `MaterialEntryPoint` 写入 `TerrainTriangleMaterials` |
| 顶点编码 | `IClusterVertexEncoder<T>` 按局部来源索引写字节 | `IClusterVertexEncode` 按 `ClusterVertexInput.SourceIndices` 写字节 |
| 编码身份 | `Compatibility` 标识字节解释 | `ClusterGpuVertexEncoder.VertexEncoding` 标识字节解释 |

两种编码器为同一用户表示写出配套字节，由材质的 `IVertexEvaluate` 解释。公共构建器处理拓扑、材质组和驻留数据；用户定义每个属性的语义及字节位置。GPU 逐三角形分组与预留容量见[GPU 几何构建](../rendering/Geometry-Features.md#gpu-几何构建)。

## 曲面与位移

用户 `IDomainEvaluate` 从三个已求值顶点和材质编号创建自己的三角形数据，并提供曲面位置、属性、重建、包围与误差。细分和着色阶段取得相同材质标量地址，具体签名与静态组合入口见[用户 Shader 阶段](../shader/User-Stage-Interfaces.md)。

位移参数由具体 Material 持有，其 `BoundsExpansion` 返回保守世界空间半径。该范围进入实例裁剪，同时用户曲面返回对应子域包围和误差。参数变化沿现有材质更新与提取流程更新实例范围；职责及空间单位的主要说明见[保守包围](../shader/Conservative-Bounds.md#材质位移范围)。

## 可运行示例

`TerrainMaterialExample.CreateCpuAsync` 和 `CreateGpuAsync` 创建相同数据的两种构建入口。八乘四米平面、红绿区域、归一化 Lift 高度字段，以及 `float3` 位置、`float2` UV、`float` Lift 的 24 字节编码都属于示例选择。Region 体素字段决定三角形材质编号；Lift 字段决定用户曲面的细节。

示例曲面在地形 Y 轴的世界空间单位方向上施加 `Displacement × Lift²` 位移。二次函数的插值误差按 Lift 值域的平方给出，重建通过沿位移方向投影回原三角形取得原始重心坐标。`TerrainDisplacementMaterial.Displacement` 使用世界米，`BoundsExpansion` 由其绝对值推导。用户 Shader 实现位于 [terrain_displacement_material.slang](../../../samples/DefaultGame/assets/Shaders/terrain_displacement_material.slang)，CPU/GPU 编码分别位于 C# 示例和 [terrain_material_build.slang](../../../samples/DefaultGame/assets/Shaders/terrain_material_build.slang)。

```csharp
using var ground = await TerrainMaterialExample.CreateGpuAsync(scheduler, contentDirectory);
ground.Install(application);
application.UpdateFrame = frame =>
{
    if (frame.Index == 0) ground.AddPlacement();
    if (frame.Index == 60) ground.RaiseLiftGpu(.5f);
    if (frame.Index == 120)
        foreach (TerrainDisplacementMaterial material in ground.Materials)
            material.Displacement = 2f;
};
await Application.RunAsync(args, application: application);
```

帧编号与参数值属于调用示例。`SaveAsync(path, materialReferences)` 把当前字段、表面及名为 `Lift`、`Region` 的材料字段绑定保存到 TerrainAsset。公开应用检查 [TerrainMaterialRenderingTests](../../../tests/SomeEngine.Runtime.Tests/TerrainMaterialRenderingTests.cs) 对照相同 CPU/GPU 字段编辑和位移参数更新的画面；固定字段版本读取的公共入口由 [TerrainMaterialFieldTests](../../../tests/SomeEngine.Resources.Tests/TerrainMaterialFieldTests.cs) 表达。
