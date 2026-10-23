# 地形资产与固定版本捕获

本页维护已实现的资产数据职责、保存格式与公开调用方法。源码入口为 [Terrain.CpuData](../../../src/SomeEngine.Resources/Terrain/Terrain.CpuData.cs)、[TerrainAsset](../../../src/SomeEngine.Assets/Schema/TerrainAsset.cs) 和 [TerrainPageSource](../../../src/SomeEngine.Assets/Pipeline/TerrainPageSource.cs)。资产遵循 [Asset 与使用对象](../Assets/Asset-Objects.md)及[数据共享](../Assets/Asset-Sharing-and-Writes.md)。

## 数据与 Block

`TerrainAsset` 使用 `.terrain.asset` 后缀和现有资产身份、Block 目录及静态编解码机制。`TerrainAsset.Duplicate` 保留身份并建立内容分支；`CreateCopy` 创建新身份。表面求值约定见[表面组合](Terrain-Surfaces.md)。

| Block | 数据 | 使用方 |
| --- | --- | --- |
| `TerrainLayoutBlock` | Terrain 版本、表面根编号、高度源和体素源的字段 ID、混合输入编号及参数 | 表面图重建 |
| `HeightFieldsBlock` | 高度字段集合、网格、默认高度、字段版本、页坐标及 chunk key | 高度读取与编辑 |
| `VoxelFieldsBlock` | 体素字段集合、网格、默认标量、字段版本、三维页坐标及 chunk key | 体素读取与编辑 |
| `TerrainMaterialBlock` | 材质资产引用、材质编号、输入名称及采样字段 ID | 应用材质解析与用户属性生成器 |

这四类 Block 保存中立数据。页面采样值通过现有 `BinaryDataWriter` 和 `BinaryDataReader` 编码为独立 chunk，字段元数据记录各页的局部 key。页坐标采用字段的全局样本页坐标，字段 ID 对应同类字段集合中的位置。源节点使用局部节点编号，重复引用在重开后保持为同一个节点对象。已编码 Cluster 几何属于独立的管线表示 Block。

`TerrainMaterialBlock.Materials` 的列表索引是局部材质编号。`HeightSources` 和 `VoxelSources` 分别保存 `MaterialIndex`、用户输入名称 `Input` 和相应字段集合的 `FieldId`。应用从重开的 Terrain 取得字段，为自己的属性生成器解释输入；材质资产引用由应用加载为普通 Material，并赋给 Placement。

源码：[表面布局](../../../src/SomeEngine.Assets/Schema/TerrainLayoutBlock.cs)、[字段集合](../../../src/SomeEngine.Assets/Schema/TerrainFieldBlocks.cs)、[材料数据](../../../src/SomeEngine.Assets/Schema/TerrainMaterialBlock.cs)。

## 捕获与更新

`Terrain.CaptureCpuAsync()` 在第一次等待前固定 Terrain 版本、所有字段数据及表面根。所选字段页经已有 `CaptureCpuRead` 路径准备 CPU 数据，其中包含 GPU 页的按需回读。完成后返回 `TerrainCpuData`：字段元数据、只读样本页，以及 Height/Voxel/Blend 的不可变表面数据。后续字段编辑继续建立自己的页面值。

`TerrainCpuData.CreateTerrain(editing)` 从这些值建立可编辑 Terrain，并恢复原版本与共享节点引用。样本数组通过普通托管引用保持有效。

`TerrainAsset.UpdateAsync(data, materials)` 从一次完整捕获生成四个 Block，并通过现有分组替换入口一起更新。`materials` 可以是材质引用列表，也可以是包含采样绑定的 `TerrainMaterialBlock`。`SaveAsync` 先复制选定资产内容，再保持来源直到文件写入完成。

下面是保存示例。名称和材质输入名称属于示例值；高度字段 `heights`、体素字段 `voxels` 和材质引用 `groundMaterial` 由调用方提供。

```csharp
ValueTask<TerrainCpuData> captured = terrain.CaptureCpuAsync();
heights.EditCpu(new HeightDeltaBrush(heights.Grid.Range, 1f));

using var materials = new TerrainMaterialBlock
{
    Materials = [groundMaterial],
    HeightSources = [new(0, "height", heights.Id)],
    VoxelSources = [new(0, "density", voxels.Id)],
};
using var asset = new TerrainAsset { Name = "ground" };
await asset.UpdateAsync(await captured, materials);
await asset.SaveAsync(path);
```

## GPU 编辑后的保存

GPU 编辑也使用同一个 `CaptureCpuAsync` 入口。调用时固定字段页版本和表面关系，所需 GPU 页通过字段的按需回读取得 CPU 数组；完成依赖对应实际 GPU 工作和复制完成。原 Terrain 在捕获之后继续提交编辑，每次编辑产生自己的字段版本。返回的 `TerrainCpuData` 保留捕获的页数组，可在应用帧结束后交给资产编码与保存。

下面演示在应用的第 0 帧提交 GPU 编辑、捕获、继续编辑，然后在应用结束后保存捕获结果。高度增量、球心、半径和体素增量属于示例参数；`terrain`、`heights`、`voxels`、两个驻留 Shader 和材质引用由应用提供，应用已安装[地形 GPU 编辑](Terrain-Gpu-Editing.md)的请求处理系统。

```csharp
Task<TerrainCpuData>? captured = null;
application.UpdateFrame = frame =>
{
    if (frame.Index == 0)
    {
        heights.EditGpu(new HeightDeltaBrushGpu(heightShader, heights.Grid.Range, 2f));
        voxels.EditGpu(new VoxelSphereDeltaBrushGpu(voxelShader, voxels.Grid, new(1, 1, 1), 2f, -1f));
        captured = terrain.CaptureCpuAsync().AsTask();

        heights.EditGpu(new HeightDeltaBrushGpu(heightShader, heights.Grid.Range, 5f));
        voxels.EditGpu(new VoxelSphereDeltaBrushGpu(voxelShader, voxels.Grid, new(1, 1, 1), 2f, 3f));
    }
};
await Application.RunAsync(args, application: application);
using var asset = new TerrainAsset { Name = "Captured GPU terrain" };
await asset.UpdateAsync(await captured!, [groundMaterial]);
await asset.SaveAsync(path);
```

保存数据的 Terrain 版本、各字段版本与表面图来自同一次捕获。重开的高度与体素按这些版本提供公开采样，Blend 的输入引用及区域参数也随捕获保存。后续编辑通过原 Terrain 的字段读取取得各自样本和版本。[TerrainGpuEditingTests](../../../tests/SomeEngine.Runtime.Tests/TerrainGpuEditingTests.cs) 中的 `GpuCaptureSavesItsFieldsAndBlendWhileTheOriginalContinuesEditing` 通过公开应用、字段和资产接口覆盖这条调用流程。

## 重开与来源生命周期

`TerrainAsset.OpenAsync` 打开资产目录。`CreateTerrainAsync(editing)` 在第一次等待前复制选定资产，读取字段元数据和全部已保存页，然后建立可编辑字段与表面图。返回的 Terrain 使用自己的样本引用，Asset 关闭后仍可读取和编辑。

两个字段 Block 的 `OpenPageSource()` 返回独立的 `TerrainPageSource`。它保留本地不可变字节或文件来源；`Retain()` 建立另一份文件访问，`ReadPageAsync(chunkKey)` 在实际读取期间保留来源，返回普通 `ReadOnlyMemory<float>`。释放来源关闭它持有的文件访问，已经返回的样本通过普通 GC 引用继续有效。文件字节读取使用统一 [AssetByteCache](../Assets/Asset-Byte-Cache.md)。

以下为重开后继续编辑的示例。标量增量 `-1`、球心和半径属于示例参数。

```csharp
using var asset = await TerrainAsset.OpenAsync(path);
Terrain restored = await asset.CreateTerrainAsync(new TerrainEditing(scheduler));
TerrainMaterialBlock bindings = await asset.GetBlockAsync<TerrainMaterialBlock>();
AssetId<MaterialAsset>[] references = bindings.Materials.ToArray();
asset.Dispose();

VoxelField detail = restored.VoxelFields[0];
TerrainEdit edit = detail.EditCpu(new VoxelSphereDeltaBrush(detail.Grid, new(4, 2, 4), 2f, -1f));
VoxelFieldRead values = await detail.ReadCpuAsync(new(4, 2, 4, 1, 1, 1));
```

[TerrainAssetExample](../../../samples/DefaultGame/TerrainAssetExample.cs) 提供可编译的 `SaveAsync` 和 `OpenAsync` 调用。重开方法调用应用提供的材质加载函数，把保存的引用解析为 Placement 使用的普通 Material。`TerrainAssetBehaviorTests` 通过公开接口检查固定捕获、节点共享、材料绑定及关闭后的读取；`TerrainAssetRenderingTests` 将重开和继续编辑后的画面与相同数据直接构建的场景比较。
