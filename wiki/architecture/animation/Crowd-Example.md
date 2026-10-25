# Crowd Example

DefaultGame 的统一 `SceneTestPlatform` 提供 `1000 characters / inline` 和
`1000 characters / cache` 两个场景。`CrowdScene.CreateScene` 提供 `TestScene` 的初始化和
工作量回调，平台持有共同的 `ApplicationScene`。普通 Game world 系统推进控制，再通过既有提取和 Cluster 路径渲染。

## 示例素材

[`CrowdExample`](../../../samples/DefaultGame/CrowdExample.cs) 是通用骨架、动作与材质
API 的程序生成示例夹具。24 段人体表面各连接四根骨骼，共 96 根；每段为 18 个纬向区间、
48 个经向区间的椭球。单角色源 LOD0 包含 19,632 顶点和 39,168 三角形。
每个顶点的四个正权重为 `102/255, 77/255, 51/255, 25/255`，全部 96 根骨骼参与网格影响与动作。

四段两秒动作 `idle`、`walk`、`run`、`turn` 通过 `QuantizedClipData.Encode(..., 60)` 生成。
一个共享 `GpuPoseStateMachine` 含四个循环状态及每对状态间 0.3 秒的过渡。
[`CrowdScene`](../../../samples/DefaultGame/CrowdScene.cs) 为每个角色创建独立
`SkeletonInstance`、GPU target、时间线和控制数组，1000 个角色共用骨架、SkinBinding、动作和状态定义。
初始相位为 `index * 2 / 1000` 秒，目标状态由 `floor((seconds + index * .037) / 1.25) % 4` 选择。
控制系统位于 Game simulation 阶段，GPU pose leaf 通过共同动画阶段推进；固定步长为 1/60 秒。

[`CrowdContent`](../../../samples/DefaultGame/Content/CrowdContent.cs) 使用项目 importer
能力生成 Cluster 几何、LOD 和关节影响盒。每段固定的影响元组随几何简化保留。
示例材质把四个 16 位 joint 和四个 8 位权重附加在标准表面属性后，使用自己的
`someengine.example.crowd.lbs4.v1` 编码标识。两条路径共用同一个 `CrowdSkin.evaluate`，
它选择 `LoadSkinJointTransform` 读取变形边界阶段已计算的当前或上一帧 `IB * G`，
再按自己的四权重求值位置和法线。该可选公共读取适用于参与 `SkinBoundsFeature` 的
蒙皮绑定，接口与适用条件见 [Skinning](Skinning.md)。
LBS 的矩阵、位置和法线中间值使用 `precise`，统一 inline 与缓存求值阶段的浮点运算顺序。
示例的 `.shader` 源码组合同时声明自己的顶点编码和 `CrowdSkin` 求值器，并预编译
`ClusterShadeMode`、`ClusterShadowReceiver` 和 `ClusterForwardShade` 的实际材质变体。
共同内容入口 `SampleContent.CreateMaterialSpecializations(depth)` 生成这份强类型集合；
角色、SkinContent 夹具与导入场景使用同一集合，导入材质按 cutoff 选择 depth 参数。
缓存实现选择每个选中顶点 48 字节，分别保存当前及上一帧的世界位置和法线，按 16 字节对齐。
示例应用通过 `ApplicationScene.DeformCacheBytes` 为两条路径都选择 256 MiB 容量。

## 构建与选择

在现有 checkout 的 Release 开发组合中生成项目内容，再构建游戏组合复制素材。
重复构建沿用已生成角色网格的资产 GUID；两条材质路径使用同一个网格身份。
将 `$queue` 指向主 checkout 的共享队列入口：

```powershell
$queue = '<primary checkout>/tools/Invoke-Benchmark.ps1'
$env:DOTNET_PROCESSOR_COUNT = '4'
& $queue --cpu --session crowd dotnet build samples/DefaultGame/DefaultGame.csproj -c Release -p:SomeEngineDevelopment=true -p:PublishAot=false -p:RuntimeIdentifier= --disable-build-servers /m:1 /nodeReuse:false /p:UseSharedCompilation=false
& $queue --session crowd samples/DefaultGame/bin/development/Release/net10.0/DefaultGame.exe sample-crowd
& $queue --cpu --session crowd dotnet build samples/DefaultGame/DefaultGame.csproj -c Release -p:PublishAot=false -p:RuntimeIdentifier= --disable-build-servers /m:1 /nodeReuse:false /p:UseSharedCompilation=false
```

修改示例材质后，开发组合的 `sample-crowd-materials` 命令更新两条材质及主项目已经构建的共同 Shader 资产，继续使用现有角色网格。

`DefaultGame --crowd-inline` 是选定的 1000 人场景入口，使用 inline 材质路径。
`DefaultGame --crowd` 显示两个角色场景，供同一配置下的双路径比较；
普通启动的选择器同时包含三个已有场景和两个角色场景。
`--regression` 选择 `1024 spheres`、`Sponza`、`Amazon Bistro`，供 `tools/Measure-Scenes.ps1` 使用。
`--measure` 在启动时测量所选集合并结束应用。

以下命令是两条变形路径采用同一配置的测量例子：

```powershell
$env:DOTNET_PROCESSOR_COUNT = '4'
$env:DOTNET_TieredCompilation = '0'
$env:DOTNET_ReadyToRun = '0'
& $queue --session crowd samples/DefaultGame/bin/game/Release/net10.0/DefaultGame.exe --crowd --measure --hardware-adapter --hidden-window --no-vsync --no-debug-ui --no-taa --no-vsm --async-compute --fixed-step 0.016666667
```

将同一命令的 `--crowd` 选择为 `--crowd-inline`，可单独运行所选 inline 场景；
其余渲染参数、固定模拟窗口和完整 CPU/GPU 采集方式相同。

角色场景的启动内容固定为 1920×1080，相机位于 `(0,30,-35)`，观察 `(0,.8,20)`，
垂直视角为 `.8` 弧度，近远面为 `40/90`，覆盖角色区域并保持关节相接表面的深度精度；
几何 LOD threshold 为 `1`。每条路径使用同一网格、变换、材质参数、
四段动作和控制规则。`--crowd` 在同一模拟进度比较两条路径：预热 64 帧，采集第 64–191 帧，
共 128 帧。`TestScene.MeasurementFrames` 接受可选的 `SimulationFrameWindow`；固定窗口
按帧数结束，并要求窗口内素材已经就绪。三个既有场景在 `test-scenes.json` 中配置
第 128–255 帧；平台也支持预热后按墙钟采集的场景。`MaximumMeasurementSeconds` 配置集合预算，
Crowd 使用固定帧窗口完成两条路径，普通三场景入口使用 45 秒预算。

`tools/Measure-Scenes.ps1 -ApplicationPath <game exe> -ApplicationArguments <arguments>`
沿相同选择器完成三个既有场景的反馈，`ApplicationArguments` 将分辨率、渲染开关与 backend
交给原有应用参数解析。`test-scenes.json` 的 `ContentDirectory` 可直接选择已有导入目录。
目录含 `scene-import.json` 时，主程序在启动时读取其来源与资产数量，通过既有
`TestScene.DescribeWorkload` 保存到场景说明，并附上实际内容目录。

已有导入场景可通过开发组合的 `reuse-scene <application root> <imported root> <output root>`
建立当前运行入口。这个示例入口保留已有 Mesh、World、Texture 身份与字节，以来源 glTF
材质参数和当前 `MaterialBlock` 写出材质，再使用主项目的共同 Shader 与管线。
既有 SCV1 页由项目内 `scene_scv1_vertex.slang` 固定解码，配套材质的光栅、阴影和着色入口
都声明 `someengine.vertex.standard.cluster.v1`。编码块原始地址的公共入口见
[Opaque Vertex Data](../cluster/Opaque-Vertex-Data.md)。内容准备耗时与完整三场景反馈耗时分别记录。

## 完成后的数据

`ApplicationScene.FrameMeasured` 提供同一 `FrameIdentity` 的 CPU 时间、GPU 时间和
`ClusterFrameMetrics`。`SimulationSeconds` 是实际累计 Game 步长；报告保存采样窗口的
首末帧和源时钟范围。平台报告保存完整帧和各 CPU 阶段的 min、mean、median、P95、max，
以及所有实际 GPU pass 的命令时间分布、命令时间总和和各实际队列的命令时间分布；
已有 `TimeGroup` 将同类变形或光栅 pass 合并。
完整 GPU 时间覆盖应用上传、pose、bounds、剔除、变形、光栅、着色、屏障和最终输出。
跨队列命令阶段可以重叠，完整 GPU 跨度由校准后的最早和最晚边界给出。

`SelectedVertices` 是两个主视图 occlusion 阶段选中 cluster 的顶点项之和，包含跨 cluster
及跨实例的重复项；`SelectedTriangles` 是相同范围的三角形项之和。
这两项由 [`cluster_bin_count.slang`](../../../assets/Shaders/cluster_bin_count.slang) 直接累计。
报告还包含各阶段软件/硬件 cluster 数、candidate 数、着色 work item 数及
`DeformCacheBytes` 的实际区域分配量（含对齐）。源 LOD0 规模和实际选中工作量分别列出。

Game 时钟达到 `8/60` 秒后，动画 Completion 阶段通过 `GpuPoseAnimation.RequestReadback`
请求角色 `0,35,70,105` 的实际状态、相位、过渡权重和 96 根模型骨骼。
平台按普通异步 Game update 推进这些系统；完成后的请求作为示例的状态多样性数据。
`TestScene.DescribeWorkload` 在场景测量完成时将该信息连同场景参数写入平台报告。

行为入口见 [`CrowdRenderingTests`](../../../tests/SomeEngine.Runtime.Tests/CrowdRenderingTests.cs)，
选择器回调入口见 [`SceneSelectionRenderingTests`](../../../tests/SomeEngine.Runtime.Tests/SceneSelectionRenderingTests.cs)。
公开图像检查可通过 `SOMEENGINE_TEST_COOKED_CONTENT` 指向已经完成 `sample-crowd` 的 DefaultGame 根目录，
直接沿用共同管线、角色网格和两个 Crowd 子目录的材质产物。
计时边界与采集开销见 [GPU Timing](../render-graph/GPU-Timing.md)。
