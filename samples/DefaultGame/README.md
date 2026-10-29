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

示例使用 32×32 个网格单元，初始边长两米，第 120 帧将宽度改为四米；尺寸、密度、更新时间、红色材质和每顶点 `float3` 位置加 `float2` UV 布局均属于示例数据。生成期间继续显示原有网格，完成后切换画面和 `GeometryRevision`。开发构建可通过以下命令运行：

```powershell
dotnet run --project samples/DefaultGame/DefaultGame.csproj -c Release -p:SomeEngineDevelopment=true -p:PublishAot=false -- --gpu-cluster --static-scene --no-vsm --no-taa
```

`HeightTerrainExample` 展示共享高度地形：`Install` 安装游戏世界的提交、分块和构建系统，`AddPlacements` 创建引用同一 Terrain 的两个放置，`Heights.EditCpu` 提交高度编辑。场景使用固定 LOD 的空间块，完成的 Mesh 由现有提取通路呈现。四米方形、每米一个采样点、两层竖向块与放置位置属于示例参数。数据职责与完整调用见[地形放置与 CPU 分块](../../wiki/architecture/terrain/Terrain-Scene.md)。

同一示例的 `AddHeightGpu(shader, delta)` 使用驻留的 `terrain_height_delta` Shader 提交 GPU 增量。字段读取和 CPU 网格构建共享 GPU 页的按需读回，完成后的几何同步到两个放置；调用路径见 [GPU 字段编辑](../../wiki/architecture/terrain/Terrain-Gpu-Editing.md)。

`GpuHeightTerrainExample` 将高度编辑、表面提取、用户属性和 Cluster 构建放在 GPU 上执行。`CreateAsync` 取得示例 Shader，`Install` 复用 TerrainScene 的批次管理，`AddPlacements` 创建两个共享放置，`AddHeight` 提交高度增量。`Scene.GetGpuBuild` 提供几何完成与渲染绑定完成句柄；字段查询通过显式 `ReadCpuAsync` 取得。网格、放置和位置/法线/UV 编码属于示例选择，接口与数据职责见 [GPU 地形几何](../../wiki/architecture/terrain/Terrain-Gpu-Geometry.md)。

`VoxelTerrainExample` 展示三维体素场的同一流程：`Install` 安装地形系统，`AddPlacement` 创建放置，`WriteSphere` 在 `SampleRegion` 内重新写入球体标量。每米一个采样点、一米半径、`[-3,4)` 的样本区域及初始球心 `(-1,0,0)` 属于示例参数。`Field.ReadCpuAsync` 取得固定版本的标量，`Scene` 的几何更新使用既有空间块及 Cluster 通路。

同一示例的 `AddSphereDeltaGpu(shader, center, radius, delta)` 使用驻留的 `terrain_voxel_delta` Shader 提交球形增量。球心与半径采用地形局部米制坐标，增量按距离线性衰减；CPU 读取和空间块构建继续使用同一字段版本与完成依赖。

`GpuVoxelTerrainExample` 使用 GPU 体素提取入口，将 `AddSphereDelta` 的设备页沿同一 TerrainScene 批次生成可见 Cluster 几何。按空间块和读取邻域准备页面，属性 shader、计数、编码及完成接口与高度示例共用。初始单位球、采样区域和位置/法线/UV 布局属于示例选择；接口见 [GPU 地形几何](../../wiki/architecture/terrain/Terrain-Gpu-Geometry.md)。

两个地形示例的 `Smooth(center, radius, neighborRadius, strength)` 与 `Flatten(center, radius, plane, strength)` 使用统一的米制半径和线性衰减；平滑的邻域半径以样本为单位，压平目标采用地形局部 `Plane`。方法返回 `TerrainEdit`，完成后可经公开字段读取采样；安装的地形系统更新对应空间块画面。固定版本邻域与自定义梯度笔刷用法见[地形邻域笔刷](../../wiki/architecture/terrain/Terrain-Brushes.md)。

对应的 `SmoothGpu(shader, center, radius, neighborRadius, strength)` 与 `FlattenGpu(shader, center, radius, plane, strength)` 使用相同操作参数。高度示例传入 `terrain_height_neighborhood` Shader，体素示例传入 `terrain_voxel_neighborhood` Shader；平滑的两个计算阶段共用固定源版本，返回的 `TerrainEdit` 覆盖最终输出。资源录制与参数对象见 [GPU 邻域笔刷](../../wiki/architecture/terrain/Terrain-Gpu-Editing.md#邻域与多阶段计算)。

`BlendTerrainExample` 从高度表面生成局部体素初值，以带渐变区域的 `BlendSurface` 组合两个源。`RaiseDetail` 继续编辑体素并更新共享表面；`Voxels.ReadCpuAsync` 读取相应样本版本。八米地表、一米采样间距、两米渐变和放置位置属于示例参数。完整接口与调用见[地形表面组合](../../wiki/architecture/terrain/Terrain-Surfaces.md)。

`GpuBlendTerrainExample` 使用同一初始表面图，通过 `AddHeight` 与 `AddSphereDelta` 在 GPU 编辑高度和体素源，再沿 TerrainScene 批次生成混合地形。`Terrain.Surface` 可接入更多高度、体素及嵌套混合节点；每个源保留自己的网格与采样间距。八米地表、一米采样间距、两米渐变和位置/法线/UV 编码属于示例选择。固定图捕获与调用见 [GPU 地形几何](../../wiki/architecture/terrain/Terrain-Gpu-Geometry.md)。

`LodTerrainExample` 展示混合地形的相机 LOD：`CreatePlacement` 设置固定根块范围和距离数组，`Install` 从应用活动相机取得位置，`RaiseDetail` 在粗细块交界附近编辑体素。32×16 米范围、4 个单元的块边长、5/9 米细分距离和笔刷球体属于示例参数。移动 `SceneEdit` 的相机后，旧块持续呈现，完成的普通网格与 Transvoxel 接缝按整批切换；调用及坐标规则见[地形 LOD](../../wiki/architecture/terrain/Terrain-Lod.md)。

`GpuLodTerrainExample` 采用相同的源数据、块范围、距离数组、相机操作和笔刷参数，通过 GPU 求值普通单元与六面过渡单元。`RaiseDetail` 返回设备编辑完成状态，`Scene.GetGpuBuild` 进一步区分整批几何完成与可见绑定完成；接口与示例见 [GPU 地形几何](../../wiki/architecture/terrain/Terrain-Gpu-Geometry.md)。

`TerrainMaterialExample.CreateCpuAsync` 与 `CreateGpuAsync` 使用相同的 Lift 高度字段、Region 体素字段、自定义顶点字节和红绿材质分组。用户曲面沿地形 Y 轴施加世界空间二次位移；`TerrainDisplacementMaterial.Displacement` 同时决定细分曲面与保守实例包围。字段编辑更新属性，材质参数更新直接改变位移；`SaveAsync` 保存字段、表面和命名材料字段绑定。归一化 Lift、24 字节编码、区域边界和二次函数属于示例选择，完整调用见[地形材质](../../wiki/architecture/terrain/Terrain-Materials.md)。

`TerrainAssetExample.SaveAsync` 固定捕获地形，再将字段、表面图和材质引用保存到 TerrainAsset。`OpenAsync` 重开可编辑 Terrain，并调用应用提供的函数解析材料引用，返回可直接用于 Placement 的材料数组。字段采样绑定、Block 划分与完整调用见[地形资产与捕获](../../wiki/architecture/terrain/Terrain-Assets.md)。

重开的 Terrain 按字段读取区域取得文件页，并持有独立来源；使用结束时调用 `Dispose`。同一示例的 `EditHeightRegionAsync` 在关闭源 Asset 后修改指定高度区域，保留材料绑定并再次保存；返回的高度读取结果继续持有已完成样本。

`TerrainAssetExample.SaveCacheAsync` 将同一字段捕获交给完整 CPU Offline Cluster 构建，并把几何页与中立数据一起保存。`OpenCacheAsync` 返回可编辑 Terrain、应用材质和已读取的缓存几何；调用 `TerrainScene.UseCache` 后添加 Placement，匹配的块立即沿普通 Mesh 驻留路径显示，局部编辑继续按批次替换相关块。调用及身份配置见[离线 Cluster 缓存](../../wiki/architecture/terrain/Terrain-Cache.md)。

统一性能验收从根级 `tools/Measure-Scenes.ps1` 进入。

实验性 GI 使用 `--experimental-gi` 启动，或在渲染选项中勾选 `Experimental GI`。材质着色查询上一帧屏幕空间探针的方向辐射，以自己的表面参数积分并合成到 `MaterialColor`；发光几何体直接光采用独立结果。射线沿屏幕空间、近景 Mesh SDF 和 Global SDF 逐级追踪，远段距离场命中直接查询 SHARC／NRC 的表面反射辐射。近景与稀疏训练共用现有材质分桶和原三角求值。

内容构建通过距离场导入组合为同一 MeshAsset 生成 `MeshDistanceFieldBlock`；实例通过已有提取把共享字段交给 Rendering world。材质的 `IGiSurfaceEvaluate` 提供法线、漫反射反照率、发光、遮蔽与覆盖率，`CLUSTER_GI_MATERIAL` 组合入口生成对应的求交、表面和发光几何求值 Shader。标准与 Unlit 材质已采用该入口。完整数据职责与帧顺序见 [GI 架构](../../docs/GI-Architecture.md)。

开发构建的 `prepare-materials` 更新默认场景的材质编译输入。`prepare-materials <场景目录>` 复用导入场景的 Shader 定义与资产构建入口，更新 Sponza、Bistro 等已有场景的材质 Shader。

`Measure-Scenes.ps1 -OutputDirectory <目录>` 指定场景测量输出目录，默认位置为系统临时目录中的 `SomeEngine/scene-measurements`。统一场景平台通过 `SOMEENGINE_SCENE_MEASUREMENTS` 接收该目录。

```powershell
# 开发组合生成样例内容。
& F:/SomeEngine.Next/tools/Invoke-Benchmark.ps1 --cpu --timeout 310 pwsh -NoProfile -File tools/Invoke-Watchdog.ps1 -- dotnet run --project samples/DefaultGame/DefaultGame.csproj -- sample

# NativeAOT 游戏组合发布到统一场景工具使用的目录。
& F:/SomeEngine.Next/tools/Invoke-Benchmark.ps1 --cpu --timeout 310 pwsh -NoProfile -File tools/Invoke-Watchdog.ps1 -- dotnet publish samples/DefaultGame/DefaultGame.csproj -c Release -o samples/DefaultGame/bin/game/Release/net10.0

& F:/SomeEngine.Next/tools/Invoke-Benchmark.ps1 --cpu --timeout 310 pwsh -NoProfile -File samples/ImportedScenes/build_scenes.ps1
& F:/SomeEngine.Next/tools/Invoke-Benchmark.ps1 --gpu --timeout 70 pwsh -NoProfile -File tools/Measure-Scenes.ps1
```
`--gpu-times <外部文件路径>` 使用现有 `ApplicationScene.FrameMeasured` 收集统一场景平台所选采样帧的 GPU 时间，保存整帧跨度、各 pass 命令时间与含屏障时间。带 GPU 时间戳的定位运行与普通性能对比分开执行。

实验构建、导入和捕获通过公共队列运行，并由 `tools/Invoke-Watchdog.ps1` 保护整个命令进程树。默认内存警戒线为 3.75 GiB、内核硬上限为 4 GiB，总时限 300 秒，CPU 与输出同时停滞 30 秒时终止。`Measure-Scenes.ps1` 已直接启用保护，保留内存储备并将输出流写入结果目录。watchdog 退出会回收全部子进程，构建也沿此进程树完成。

`Measure-Scenes.ps1` 的场景进程默认使用 8 GiB 内存限额，`-MemoryMB` 可指定本轮预算。`-Scene` 选择配置中的同名场景，默认执行全部场景。`--gpu-times <path>` 通过已有 GPU 时间戳输出各阶段耗时；普通限帧运行标记为 `PresentedFrames`，统一测量窗口标记为 `Measurement`。
