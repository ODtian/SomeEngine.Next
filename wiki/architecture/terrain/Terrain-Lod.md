# 地形 LOD 与过渡接缝

本页维护已实现的空间选择、CPU 表面算法与公开调用方法。源码入口为 [TerrainLodSelection](../../../src/SomeEngine.Terrain/TerrainLodSelection.cs)、[TerrainScene](../../../src/SomeEngine.Terrain/TerrainScene.cs)、[MarchingCubesTerrainMesher](../../../src/SomeEngine.Terrain/Meshing/MarchingCubesTerrainMesher.cs) 和[过渡单元](../../../src/SomeEngine.Terrain/Meshing/MarchingCubesTerrainMesher.Transitions.cs)。放置、字段变化与提取通路由[放置与分块](Terrain-Scene.md)维护。

## 固定空间范围与相机输入

`TerrainChunkGrid` 指定每边单元数和最细采样间距。LOD 为 `k` 时，单元间距为最细间距乘以 `2^k`，块坐标使用这一层级的网格。

`TerrainPlacement(terrain, chunks, materials, lod)` 选取固定层级的块。接收 `TerrainLodDistances` 的构造函数以距离数组长度作为根层级；`chunks` 表达该层级上的固定根块范围。数组依次给出 LOD 1、2 及更高层级的细分距离，单位为世界空间米。

`TerrainLodSystem` 读取相机位置，将每个块的八角通过放置变换转换为世界包围盒。相机到该包围盒的距离小于对应阈值时，将块细分为八个子块并继续选择。所有替换块覆盖相同的根范围。随后比较面邻居，把层级差大于一的粗块继续细分，直到面邻接满足 2∶1；粗块的六面标志记录相邻细块所在方向。

`TerrainLodSystem(scene, cameraPosition)` 接收只读位置提供函数。应用安装的 `TerrainGeometryFeature` 通过 `ApplicationGeometryContext.CameraPosition` 读取 `RuntimeScene.Camera`，在游戏系统执行时取得当前位置。公开 `SceneEdit.SetCameraTransform` 更新这个相机，因此下一次游戏更新使用新的视点。

## 配套网格输入

`ITerrainMesher.Build` 的 `TerrainChunkBounds` 包含块坐标、单元数、当前单元间距、`Lod` 与 `TransitionFaces`，并提供 `FinestCellSize`。一个算法入口共同生成普通单元和接缝。`TerrainChunk` 公开当前已提交的层级与过渡面；几何共享键同时包含 Terrain、坐标、层级和过渡面配置。

默认算法采用 [Eric Lengyel 的 Transvoxel](https://transvoxel.org/) 普通表和过渡表；MIT 许可位于源码同目录。普通单元以 X/Y/Z 的位 0/1/2 编号八个角点。过渡面的九个细采样按三行三列编号，case 权重依次为 `1, 2, 4, 128, 256, 8, 64, 32, 16`，四个粗角点取细采样 `0, 2, 6, 8`。过渡类的最高位控制三角形反向。

粗分辨率边上的交点递归二分到最细格距，按符号选择仍包含过零点的半边，然后线性插值。所有层级的法线都使用最细格距的中心差分。过渡面的局部两个正轴按叉积指向粗块内部排列，六面使用相同查表约定。

粗块在过渡面附近留出当前单元宽度四分之一的空间。普通单元顶点与过渡单元粗侧顶点使用同一个边界位移，并把位移投影到原始顶点的切平面。位于块边或角附近的顶点在所有相关面都连接细邻居时选取位移位置；其他组合保持主位置。过渡单元细侧顶点保留采样交点，因主位置选择而重合的三角形顶点被折叠。属性生成器取得的 `TerrainSurfacePoint` 始终记录原始采样位置和对应法线。

## 完整批次切换

`TerrainScene` 在同一个构建批次中保存选定块及其过渡面、放置变换、材质数组和固定版本的表面输入。字段编辑的影响范围包含算法及属性读取邻域；接缝面改变产生对应的新几何输入。已提交块持续作为当前画面使用。

每个 Terrain 的批次全部完成后，`TerrainCommitSystem` 在同一次游戏更新中应用 Mesh、版本、实体增加与移除，以及所捕获的放置数据。后续提取观察到这一完整结果。构建期间到达的新相机位置和字段编辑进入接续批次。首次放置的零版本块提供既有 `TerrainSource` 提取入口，完成后取得构建版本。

`GetChunks` 读取已提交块；`WaitForBuildsAsync` 等待已提交的构建任务，下一次提交系统更新应用结果。`TerrainLodTests` 通过公开网格检查粗细球面闭合，通过公开块结果检查多级邻接、覆盖范围及延迟输入完成后的整体替换。

## 公开示例

[LodTerrainExample](../../../samples/DefaultGame/LodTerrainExample.cs) 创建高度与体素混合源。32×16 米根范围、每边四个单元、5/9 米细分距离、相机位置和编辑帧属于示例参数。

```csharp
var ground = new LodTerrainExample(scheduler);
ground.Install(application);
application.UpdateFrame = frame =>
{
    if (frame.Index == 0)
    {
        ground.Scene.Add(ground.CreatePlacement(material));
        frame.Scene.SetCameraTransform(new(-10, 4, 6), Quaternion.CreateFromYawPitchRoll(0, .45f, 0));
    }
    if (frame.Index == 60)
    {
        frame.Scene.SetCameraTransform(new(0, 10, -14), Quaternion.CreateFromYawPitchRoll(0, .33f, 0));
        ground.RaiseDetail();
    }
};
await Application.RunAsync(args, application: application);
```

`TerrainLodRenderingTests` 通过公开应用入口移动相机并修改细节，将完成后的画面与直接创建相同最终状态的场景比较。
