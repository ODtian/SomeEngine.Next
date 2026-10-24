# Crowd Example

DefaultGame 的统一 `SceneTestPlatform` 提供 `1000 characters / inline` 和
`1000 characters / cache` 两个场景。它们通过 `TestScene.CreateApplication` 建立
`ApplicationScene`，由普通 Game world 系统推进控制，再通过既有提取和 Cluster 路径渲染。

## 示例素材

[`CrowdExample`](../../../templates/DefaultProject/Game/CrowdExample.cs) 是通用骨架、动作与材质
API 的程序生成示例夹具。24 段人体表面各连接四根骨骼，共 96 根；每段为 18 个纬向区间、
48 个经向区间的椭球。单角色源 LOD0 包含 19,632 顶点和 39,168 三角形。
每个顶点的四个正权重为 `102/255, 77/255, 51/255, 25/255`，全部 96 根骨骼参与网格影响与动作。

四段两秒动作 `idle`、`walk`、`run`、`turn` 通过 `QuantizedClipData.Encode(..., 60)` 生成。
一个共享 `GpuPoseStateMachine` 含四个循环状态及每对状态间 0.3 秒的过渡。
[`CrowdScene`](../../../templates/DefaultProject/Game.App/CrowdScene.cs) 为每个角色创建独立
`SkeletonInstance`、GPU target、时间线和控制数组，1000 个角色共用骨架、SkinBinding、动作和状态定义。
初始相位为 `index * 2 / 1000` 秒，目标状态由 `floor((seconds + index * .037) / 1.25) % 4` 选择。
控制系统位于 Game simulation 阶段，GPU pose leaf 通过共同动画阶段推进；固定步长为 1/60 秒。

[`CrowdContent`](../../../templates/DefaultProject/Game.Build/CrowdContent.cs) 使用项目 importer
能力生成 Cluster 几何、LOD 和关节影响盒。每段固定的影响元组随几何简化保留。
示例材质把四个 16 位 joint 和四个 8 位权重附加在标准表面属性后，使用自己的
`someengine.example.crowd.lbs4.v1` 编码标识。两条路径共用同一个 `CrowdSkin.evaluate`，
它通过 `LoadSkinTransform` 读取当前或上一帧模型骨骼，并求值位置和法线。
缓存实现选择每个选中顶点 48 字节，分别保存当前及上一帧的世界位置和法线，按 16 字节对齐。
示例应用通过 `ApplicationScene.DeformCacheBytes` 为两条路径都选择 256 MiB 容量。

## 构建与选择

在现有 checkout 的普通 Release 输出中构建项目内容，再构建 `Game.App` 复制素材。
将 `$queue` 指向主 checkout 的共享队列入口：

```powershell
$queue = '<primary checkout>/tools/Invoke-Benchmark.ps1'
$env:DOTNET_PROCESSOR_COUNT = '4'
& $queue --cpu --session crowd dotnet build templates/DefaultProject/Game.Build/Game.Build.csproj -c Release --no-restore --disable-build-servers /m:1 /nodeReuse:false /p:UseSharedCompilation=false
& $queue --session crowd templates/DefaultProject/Game.Build/bin/Release/net10.0/Game.Build.exe --crowd templates/DefaultProject .
& $queue --cpu --session crowd dotnet build templates/DefaultProject/Game.App/Game.App.csproj -c Release --no-restore --disable-build-servers /m:1 /nodeReuse:false /p:UseSharedCompilation=false
```

`Game.App --crowd` 显示两个角色场景；普通启动的选择器同时包含三个已有场景和两个角色场景。
`--regression` 选择 `1024 spheres`、`Sponza`、`Amazon Bistro`，供 `tools/Measure-Scenes.ps1` 使用。
`--measure` 在启动时测量所选集合并结束应用。

以下命令是两条变形路径采用同一配置的测量例子：

```powershell
$env:DOTNET_PROCESSOR_COUNT = '4'
$env:DOTNET_TieredCompilation = '0'
$env:DOTNET_ReadyToRun = '0'
& $queue --session crowd templates/DefaultProject/Game.App/bin/Release/net10.0/Game.App.exe --crowd --measure --hardware-adapter --hidden-window --no-vsync --no-debug-ui --no-taa --no-vsm --async-compute --fixed-step 0.016666667
```

角色场景的启动内容固定为 1920×1080，相机位于 `(0,30,-35)`，观察 `(0,.8,20)`，
垂直视角为 `.8` 弧度，近远面为 `40/90`，覆盖角色区域并保持关节相接表面的深度精度；
几何 LOD threshold 为 `1`。每条路径使用同一网格、变换、材质参数、
四段动作和控制规则。`--crowd` 在同一模拟进度比较两条路径：预热 64 帧，采集第 64–191 帧，
共 128 帧。`SceneTestPlatform.MeasurementFrames` 接受可选的 `SceneFrameRange`；固定窗口
按帧数结束，并要求窗口内素材已经就绪。三场景反馈继续等待 16 个就绪帧及至少 3 秒预热，
再采集 2 秒，集合时间预算为 45 秒。

## 完成后的数据

`ApplicationScene.FrameMeasured` 提供同一 `FrameIdentity` 的 CPU 时间、GPU 时间和
`ClusterFrameMetrics`。`SimulationSeconds` 是实际累计 Game 步长；报告保存采样窗口的
首末帧和源时钟范围。平台报告保存完整帧和各 CPU 阶段的 min、mean、median、P95、max，
以及所有实际 GPU pass 的命令时间分布；已有 `TimeGroup` 将同类变形或光栅 pass 合并。
完整 GPU 时间覆盖应用上传、pose、bounds、剔除、变形、光栅、着色、屏障和最终输出。
跨队列命令阶段可以重叠，完整 GPU 跨度由校准后的最早和最晚边界给出。

`SelectedVertices` 是两个主视图 occlusion 阶段选中 cluster 的顶点项之和，包含跨 cluster
及跨实例的重复项；`SelectedTriangles` 是相同范围的三角形项之和。
这两项由 [`cluster_bin_count.slang`](../../../assets/Shaders/cluster_bin_count.slang) 直接累计。
报告还包含各阶段软件/硬件 cluster 数、candidate 数、着色 work item 数及
`DeformCacheBytes` 的实际区域分配量（含对齐）。源 LOD0 规模和实际选中工作量分别列出。

第八帧通过公开 GPU pose readback 采集角色 `0,35,70,105` 的实际状态、相位、过渡权重和
96 根模型骨骼，作为示例的状态多样性数据。应用的 `DescribeWorkload` 在场景测量完成时
将该信息连同场景参数写入平台报告。

行为入口见 [`CrowdRenderingTests`](../../../tests/SomeEngine.Runtime.Tests/CrowdRenderingTests.cs)，
选择器回调入口见 [`SceneSelectionRenderingTests`](../../../tests/SomeEngine.Runtime.Tests/SceneSelectionRenderingTests.cs)。
计时边界与采集开销见 [GPU Timing](../render-graph/GPU-Timing.md)。
