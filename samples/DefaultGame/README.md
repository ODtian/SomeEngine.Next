# DefaultGame

DefaultGame 是引擎的统一渲染样例。1024 小球场景、Sponza、Amazon Bistro 的导入配置、测试面板和内容生成代码由本目录持有。

游戏项目编译引擎运行能力；开发构建同时编译 `Content` 中的样例资产生成与自定义导入。标准材质、标准导入和 Shader 构建使用引擎实现。

项目 Shader 定义和顶层组合入口位于 `assets/Shaders`。各管线入口封装自己的资源和执行依赖，材质使用阶段输入完成求值。

`ProceduralClusterExample` 展示程序几何进入 Cluster 的公开调用：位置、`StandardClusterAttributes`、索引和 `ClusterMaterialGroup` 交给 `ClusterMeshBuilder.Build`，指定编码器与 `Realtime` 或 `Offline` 策略后取得 `Mesh`。`Add` 方法在应用帧回调中通过 `SceneEdit.AddEntity` 添加两个材质区段。四米宽、两米高的面片及其 UV 属于示例参数。

```csharp
application.UpdateFrame = frame =>
{
    if (frame.Index == 0)
        ProceduralClusterExample.Add(frame.Scene, firstMaterial, secondMaterial);
};
await Application.RunAsync(args, application: application);
```

实时策略构建叶子 cluster 与 BVH；离线策略执行顶点焊接、网格优化、简化和 LOD 层级构建。调用方的属性类型通过 `IClusterVertexEncoder<TAttributes>` 编码，配套材质用 `IVertexEvaluate` 解释相同的 `VertexEncoding`。Asset 导入入口 `ClusterBuilder.ProcessRaw` 与 `ProcessMesh` 使用同一 CPU 构建核心；`ClusterBuilder.CreateAsset` 可将完成的 Mesh 连同材质引用保存为 Asset。

`GpuClusterExample` 展示通过公开应用入口生成并渲染 GPU 几何。构建输入、配套编码器、图内顺序与范围回收见 [GPU 几何构建](../../wiki/architecture/rendering/Geometry-Features.md#gpu-几何构建)。

示例中的两米正方形、红色材质、每顶点 `float3` 位置与 `float2` UV 布局均属于示例数据。开发构建可通过以下命令运行：

```powershell
dotnet run --project samples/DefaultGame/DefaultGame.csproj -c Release -p:SomeEngineDevelopment=true -p:PublishAot=false -- --gpu-cluster --static-scene --no-vsm --no-taa
```

`HeightTerrainExample` 展示共享高度地形：`Install` 安装游戏世界的提交、分块和构建系统，`AddPlacements` 创建引用同一 Terrain 的两个放置，`Heights.EditCpu` 提交高度编辑。场景使用固定 LOD 的空间块，完成的 Mesh 由现有提取通路呈现。四米方形、每米一个采样点、两层竖向块与放置位置属于示例参数。数据职责与完整调用见[地形放置与 CPU 分块](../../wiki/architecture/terrain/Terrain-Scene.md)。

同一示例的 `AddHeightGpu(shader, delta)` 使用驻留的 `terrain_height_delta` Shader 提交 GPU 增量。字段读取和 CPU 网格构建共享 GPU 页的按需读回，完成后的几何同步到两个放置；调用路径见 [GPU 高度编辑](../../wiki/architecture/terrain/Terrain-Gpu-Editing.md)。

统一性能验收从根级 `tools/Measure-Scenes.ps1` 进入。

```powershell
# 开发组合生成样例内容。
dotnet run --project samples/DefaultGame/DefaultGame.csproj -- sample

# NativeAOT 游戏组合发布到统一场景工具使用的目录。
dotnet publish samples/DefaultGame/DefaultGame.csproj -c Release -o samples/DefaultGame/bin/game/Release/net10.0

./samples/ImportedScenes/build_scenes.ps1
./tools/Measure-Scenes.ps1
```
