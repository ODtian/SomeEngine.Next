# 体素距离重建

本页维护已实现的距离算法、数据职责和公共调用方法（Wiki 类别 1、2）。入口为 [VoxelDistance](../../../src/SomeEngine.Resources/Terrain/VoxelDistance.cs) 与 [VoxelDistanceGpu](../../../src/SomeEngine.Render/Terrain/VoxelDistanceGpu.cs)，默认实现为 [SubdividedVoxelDistance](../../../src/SomeEngine.Resources/Terrain/SubdividedVoxelDistance.cs)、[SubdividedVoxelDistanceGpu](../../../src/SomeEngine.Render/Terrain/SubdividedVoxelDistanceGpu.cs) 和 [terrain_voxel_distance.slang](../../../assets/Shaders/terrain_voxel_distance.slang)。字段坐标、版本与读取由 [CPU 字段](Terrain-Fields.md)维护，GPU 请求与实际完成由 [GPU 字段编辑](Terrain-Gpu-Editing.md)维护。

## 区域与精度

`VoxelField.RebuildDistanceCpu(settings, algorithm)` 与 `RebuildDistanceGpu(settings, algorithm)` 把一个固定输入版本重建为新的字段版本，返回 `TerrainEdit`。普通 CPU 重载采用 `SubdividedVoxelDistance`；接受驻留 `Shader` 的 GPU 重载采用 `SubdividedVoxelDistanceGpu`。

| 参数 | 单位 | 含义 |
| --- | --- | --- |
| `VoxelDistanceSettings.Range` | 全局样本坐标 | 本次写入的样本区域 |
| `ReadBorder` | 样本 | 在写入区域各方向扩展的输入邻域 |
| `ReadRegion` | 全局样本坐标 | `Range.Expand(ReadBorder)`，固定输入的完整读取区域 |
| `Precision.DistanceTolerance` | 米 | 每个写入样本到固定输入零面的距离误差上限 |
| `Precision.SurfaceTolerance` | 米 | 写入区域内完整网格边的零交点位移上限 |

求距目标是 `ReadRegion` 内完整三线性单元的零等值集合。调用方选择覆盖所需最近界面的读取邻域。结果沿源样本的符号保存距离幅值，负值表示实心。任意位置的 `VoxelFieldRead.Sample` 继续按格距进行三线性插值，格内曲面的离散精度同时由该格距与已有样本决定。

零面精度覆盖 `Range` 内样本组成的完整单元体积：其米制端点为 `Range.Min × SampleSpacing` 与 `(Range.Max - 1) × SampleSpacing`。位置检查使用两端都在该样本网格内的边；源值为 `a,b` 时交点比例为 `a / (a - b)`，结果值使用相同公式，比例差乘以格距得到米制位移。原样本符号及原有零值共同参加检查。以相同符号案例和边连接方式提取的三角形，其对应顶点继承这项位置界。写入范围外的样本继续保持原字段值，参与外侧单元的下一段输入区域。

## 默认有界细分算法

默认算法先固定包含零面的源单元，再为每个输出样本搜索最近零点。三线性函数在子盒中的值处于其八个角值的最小值与最大值之间；源单元和后续子盒据此参与搜索。

样本到子盒的最短距离给出下界，子盒边上的线性零交点给出候选上界。算法保留最小候选距离，并对仍能改进该距离的子盒继续细分。当所有候选盒对应的上下界差满足 `DistanceTolerance` 时，求距满足该样本的数值精度。CPU 与 GPU 使用相同的单元顺序、边交点规则和细分条件。

`SubdividedVoxelDistance(maxSubdivisions)` 与 `SubdividedVoxelDistanceGpu(shader, maxSubdivisions)` 分别配置默认算法的细分预算，默认值为 10。单精度实现使用至多 20 层细分；每层把一个单元沿三轴各分为两份。预算结束时仍保留实际距离界，用它决定本次结果是否满足精度。区间细分的数学背景可参考 [Burr、Gao、Tsigaridas 的原论文](https://arxiv.org/abs/1801.05864)；当前求距步骤以上述源码为准。

CPU 在一次任务中建立固定源单元并计算区域样本，之后由已有字段编辑服务按页写入。GPU 在当前图扩展中准备源单元、计算区域距离、汇总精度、写出各页；这些阶段通过 [TerrainBrushCompute](../../../src/SomeEngine.Render/Terrain/TerrainBrushCompute.cs) 录制。各输出样本共用同一份固定源，输出页保留写入区域以外的原值。

## 完成与算法替换

`TerrainEdit.WaitAsync()` 等待新字段版本和精度核对完成。CPU 计算的精度核对位于新页写入任务之前；GPU 精度报告通过已有设备读回流程取得，在实际队列完成后检查，再完成字段依赖。后续 CPU 读取、继续编辑和保存沿同一个完成结果推进。

超出距离或零面容差时，完成结果传播 `VoxelDistancePrecisionException`。异常公开 `DistanceError`、`SurfaceShift` 和请求的 `Precision`，分别表示本次距离界、零交点位移和容差。数值预算与求解精度因此保持各自的含义。

`IVoxelDistanceCpu.Rebuild` 接收固定 `VoxelFieldRead`、通用区域与精度配置，以及按 X 最快排列的目标样本跨度。算法填满 `Range` 对应的有符号距离，并返回最大距离误差界；字段服务核对源符号和零交点位移，然后提交该版本的完成结果。

`IVoxelDistanceGpu.Record` 接收区域级 `VoxelDistanceGpuContext`：固定源样本、页表、网格、区域、输出页集合和精度报告缓冲区。各输出页写入完整内容，`Accuracy` 写入两个 `float`，依次表示米制距离误差界和零交点位移。`AddCompute` 接受参数及显式资源绑定，并把当前阶段排在前一阶段之后。自定义算法由此保留自己的计算步骤，输入取得、设备页持有、报告读回和 `TerrainEdit` 完成继续由字段系统承担。

## 调用示例

以下区域、四样本邻域、5 毫米距离容差、1 厘米零面容差及 9 层细分属于示例参数。`cpuField` 与 `gpuField` 由调用方提供，`distanceShader` 来自 [Shader 资产定义](../../../assets/Shaders/terrain_voxel_distance.shader)。

```csharp
var range = new VoxelRegion(-2, -2, -1, 5, 5, 3);
var precision = new VoxelDistancePrecision(.005f, .01f);
var settings = new VoxelDistanceSettings(range, 4, precision);

TerrainEdit cpuEdit = cpuField.RebuildDistanceCpu(settings, new SubdividedVoxelDistance(9));
await cpuEdit.WaitAsync();
VoxelFieldRead cpuDistances = await cpuField.ReadCpuAsync(range);

TerrainEdit gpuEdit = gpuField.RebuildDistanceGpu(settings, new SubdividedVoxelDistanceGpu(distanceShader, 9));
ValueTask<VoxelFieldRead> gpuDistances = gpuField.ReadCpuAsync(range);
```

GPU 调用由已经安装地形功能的应用帧处理，调用方在应用继续推进时异步等待 `gpuEdit` 或 `gpuDistances`。两个重载也接受外部 `JobHandle`，并共同依赖字段的前一次编辑。调用时已取得的字段读取继续持有自己的版本。

[VoxelDistanceTests](../../../tests/SomeEngine.Resources.Tests/VoxelDistanceTests.cs) 使用可计算距离的斜平面检查固定来源、零交点和后续版本；[TerrainDistanceGpuTests](../../../tests/SomeEngine.Runtime.Tests/TerrainDistanceGpuTests.cs) 在公开应用中用相同区域和参数对照 CPU/GPU。斜平面及沿第三轴变化的源标量缩放属于验收例子。

返回[地形导航](../Terrain.md)。
