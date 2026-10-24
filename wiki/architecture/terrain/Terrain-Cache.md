# 地形离线 Cluster 缓存

本页维护已实现的离线构建、缓存关联及公开保存和重开接口。源码入口为 [TerrainClusterCache](../../../src/SomeEngine.Terrain/TerrainClusterCache.cs)、[TerrainClusterBlock](../../../src/SomeEngine.Assets/Schema/TerrainClusterBlock.cs)、[TerrainClusterData](../../../src/SomeEngine.Assets/Schema/TerrainClusterData.cs) 和 [TerrainScene](../../../src/SomeEngine.Terrain/TerrainScene.cs)。中立字段与表面数据由[地形资产](Terrain-Assets.md)持有，空间分块与批次提交见[放置与分块](Terrain-Scene.md)。

## 从固定字段到离线几何

`TerrainClusterCache.BuildAsync` 接收已完成的 `TerrainCpuData`、`TerrainEditing`、显式 `TerrainChunkBounds` 集合、用户属性生成器、匹配的编码器和生成身份。它从捕获值创建自己的 Terrain，使用成对的 `ITerrainMesher` 构建普通单元和过渡接缝，再通过 `TerrainClusterAdapter<TAttributes>` 的 `Offline` 策略进入已有 CPU Cluster 构建核心。

完整离线路径包含顶点焊接、拓扑优化、简化和 LOD 层级生成。最终保存的页、BVH、最低细节页索引、包围体和材质区段来自该路径返回的普通 `Mesh`。算法和编码入口见 [ClusterMeshBuilder.Input](../../../src/SomeEngine.Geometry.Cluster/ClusterMeshBuilder.Input.cs)。

`TerrainGeometryIdentity` 的 `Mesher` 和 `Attributes` 由调用方指定，表示所用算法、版本及影响结果的配置。配置改变时，调用方更新相应身份值。`VertexEncoding` 直接取自配套编码器的 `Compatibility`；读取邻域与每块的坐标、单元数、间距、LOD、过渡面也随数据保存。

## 来源关联

`TerrainClusterBlock` 是资产中的独立管线表示。它保存以下数据：

| 数据 | 来源与用途 |
| --- | --- |
| 表面图与参数 | 来自固定 `TerrainCpuData.Surface`，用于核对高度、体素和混合节点关系 |
| 字段网格、默认值与版本 | 来自根表面及属性声明使用的字段，记录几何输入 |
| 字段页坐标、`Id` 与 `Revision` | 来自捕获页，核对每个分块采样范围中的页面值 |
| 生成身份、编码标识与读取邻域 | 来自调用方、属性生成器、编码器和 mesher，核对构建配置 |
| 分块与 Mesh 数据 | 包含空间选择、过渡面、生成版本及现有页/BVH 表示 |

`ITerrainAttributeGenerator<TAttributes>.Fields` 通过 `TerrainFieldInputs` 声明额外高度字段和体素字段 ID。缓存关联与 Scene 的脏块选择使用根表面字段和此声明的并集。

`TerrainClusterData.MatchesSources` 在 Terrain 的数据锁内核对表面图、字段网格和默认值，并根据分块范围、读取邻域和字段插值所需样本核对页面身份及版本。范围内原先为空的页也参与比较，后续在该位置创建页会使相应块重新生成。字段的整体版本保存来源信息；块是否可复用由其实际采样范围中的页决定，因此其他区域的编辑可以继续使用缓存几何。

## 保存与重开

同一个固定捕获分别用于 `TerrainAsset.UpdateAsync` 和离线构建，再把返回的 `TerrainClusterBlock` 通过 `SetBlock` 加入资产。以下是调用示例；块尺寸、范围和身份字符串属于示例参数，`attributes`、`encoder` 与 `groundMaterial` 由应用提供。

```csharp
TerrainCpuData values = await terrain.CaptureCpuAsync();
var grid = new TerrainChunkGrid(64, .125f);
TerrainChunkBounds[] chunks =
[
    grid.GetBounds(new(0, 0, 0), 0),
    grid.GetBounds(new(1, 0, 0), 0),
];
var identity = new TerrainGeometryIdentity("Transvoxel/1", "Ground attributes/1");
using TerrainClusterBlock geometry = await TerrainClusterCache.BuildAsync(
    values, editing, chunks, attributes, encoder, identity);
using var asset = new TerrainAsset { Name = "Cached ground" };
await asset.UpdateAsync(values, [groundMaterial]);
asset.SetBlock(geometry);
await asset.SaveAsync(path);
```

`TerrainClusterBlock.CreateAsync()` 取得缓存条目的完整页与 BVH，返回 `TerrainClusterData` 中的普通 Mesh。几何字节由托管对象持有，读取完成后可关闭 Asset；返回 Terrain 的字段文件来源仍按[地形资产生命周期](Terrain-Assets.md#重开与来源生命周期)管理。

```csharp
using TerrainAsset opened = await TerrainAsset.OpenAsync(path);
using Terrain terrain = await opened.CreateTerrainAsync(editing);
TerrainClusterData cache = await (await opened.GetBlockAsync<TerrainClusterBlock>()).CreateAsync();
opened.Dispose();

var adapter = new TerrainClusterAdapter<GroundAttributes>(attributes, encoder, ClusterBuildStrategy.Realtime);
var scene = new TerrainScene(adapter, grid);
int reused = scene.UseCache(terrain, cache, identity);
int placement = scene.Add(new TerrainPlacement(terrain, new(0, 0, 0, 2, 1, 1), [material]));
```

上例的 `GroundAttributes` 表示应用自己的顶点属性数据类型，`attributes` 实现对应的 `ITerrainAttributeGenerator<GroundAttributes>`。`UseCache` 在添加 Placement 前调用，返回已接纳的块数；其余块使用 Scene 已配置的生成流程。已接纳几何由 Scene 原有几何集合持有，随后成为普通 `MeshInstance`，沿既有提取与 Cluster 驻留路径显示。

局部编辑继续使用 TerrainScene 的批次构建。相关块的旧 Mesh 持续显示到新批次完成，之后更新 Mesh 内容与块版本；其余块保留已完成的几何。缓存值与 Scene 使用的 Mesh 各自持有对象身份，因此 Scene 后续更新保留原缓存数据供再次匹配。

[TerrainAssetExample](../../../samples/DefaultGame/TerrainAssetExample.cs) 提供可编译的 `SaveCacheAsync` 与 `OpenCacheAsync`，同时处理材质引用与应用材料加载。保存方法还返回已生成的 `TerrainClusterData`，供当前应用直接使用。公共行为由 [TerrainCacheTests](../../../tests/SomeEngine.Terrain.Tests/TerrainCacheTests.cs) 和 [TerrainCacheRenderingTests](../../../tests/SomeEngine.Runtime.Tests/TerrainCacheRenderingTests.cs) 覆盖：保存重开、离线 LOD 页、局部版本替换，以及保存前的离线几何与重开几何在相同编辑下的应用画面。
