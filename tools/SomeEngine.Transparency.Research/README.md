# 透明 visibility 与 OIT 研究

以下启动与测量命令通过 `F:/SomeEngine.Next/tools/Invoke-Benchmark.ps1` 共享队列提交，结果用于当轮读取。调用方读取 `artifacts` 下的结果后立即删除，图像、日志与临时参考源码沿同一使用周期清理。

2026-10-16 的研究分支为 `codex/tile-effects`，基线已 rebase 到渲染性能分支的 `bb453878`。当前 DefaultGame 入口由 `templates/DefaultProject/Game.App` 提供。效果专项与统一三场景的本轮设置、结果见 [新场景与性能基线](../../wiki/architecture/rendering/Transparency-Tile-Visibility.md#2026-10-16-性能基线与效果场景)。

当前实现采用 16×16 tile、共享图元缓存与每像素寄存器队列，提供硬件光栅和软件光栅两条入口。软件覆盖与合成融合为一个 compute pass，硬件路径使用 early depth、wave 合并覆盖与同一合成器。排序通过 wave shuffle 交换局部键，通过 shared memory 交换跨 wave 引用，计数复用排序存储。处理持续到几何耗尽或剩余颜色贡献满足误差阈值。实现、片上数据分工和同机证据见 [Tile visibility OIT](../../wiki/architecture/rendering/Transparency-Tile-Visibility.md)。

覆盖树版本同时保留作为演进对照。完整几何形成连续树，投影互斥证明支持一次覆盖区间，命中后直接跳过整段；深度交叠部分按最近覆盖点推进。覆盖方程、树节点、覆盖区间和共享颜色表存储于一块 GPU 缓冲，逐像素状态保存在寄存器中，颜色直接合成进现有场景图。

通用材质的主要评价采用模型内部随光照变化的颜色，常色合并收益独立作为专项优化记录。

实现和当次证据见 [透明覆盖遍历 OIT](../../wiki/architecture/rendering/Transparency-Coverage-Traversal.md)。

运行颜色与 GPU 时间测量：

```powershell
& 'F:/SomeEngine.Next/tools/Invoke-Benchmark.ps1' dotnet run --project tools/SomeEngine.Transparency.Measurements/SomeEngine.Transparency.Measurements.csproj -c Release -- artifacts/transparency-research.json
```

运行两种 tile 光栅、覆盖树遍历、WBOIT、四阶与六阶 FP32 moments、完整片元 PPLL 排序的同负载对照：

```powershell
& 'F:/SomeEngine.Next/tools/Invoke-Benchmark.ps1' dotnet run --project tools/SomeEngine.Transparency.Measurements/SomeEngine.Transparency.Measurements.csproj -c Release -- --compare artifacts/transparency-comparison.json
```

该命令测量十三种透明负载与八种实现。GPU 计时为 1280×720，图像误差为 128×128，参考为全部覆盖点的标准排序合成。各方法包含自身清空、透明绘制、屏障与最终合成；固定投影几何和共同背景由测量入口交付。每组先累计至少 250 ms GPU 预热，各方法再连续提交 32 帧预热和 24 帧采样。异色交叠球体同时检查重复帧与倒序提交的输出颜色。

首轮八种方法、本轮 11 个优化配置及最终 52 条配对结果的文字结论见 [Tile visibility OIT](../../wiki/architecture/rendering/Transparency-Tile-Visibility.md)。历史实验通过既有[研究提交 ce13d11d](https://github.com/ODtian/SomeEngine.Next/commit/ce13d11d)读取。

将待比较的 resolver shader 路径传给 `--tile-reference`，测量入口会在当前硬件与软件实现之前分别执行对应参考版本，使用同一份几何、质量参考及预热方式：

```powershell
& 'F:/SomeEngine.Next/tools/Invoke-Benchmark.ps1' dotnet run --project tools/SomeEngine.Transparency.Measurements/SomeEngine.Transparency.Measurements.csproj -c Release -- --compare artifacts/tile-comparison.json --tiles-only --tile-reference=C:/OitReference/tile_visibility_resolve.slang
```

参考 shader 的 include 从研究项目 shader 目录解析，传入的文件保存 resolver 主体。结果中的 `ReferenceShaderSource` 同时保存参考源码；最终优化报告的最后一项实验包含本轮原版源码。调用方读取当轮结果后，立即清理 JSON 与临时参考源码。

Lucid 原生参考与 tile 路径采用同一 RGBA8 输入时，分别执行：

```powershell
& 'F:/SomeEngine.Next/tools/Invoke-Benchmark.ps1' python tools/SomeEngine.Transparency.Research/References/MeasureLucid.py
& 'F:/SomeEngine.Next/tools/Invoke-Benchmark.ps1' dotnet run --project tools/SomeEngine.Transparency.Measurements/SomeEngine.Transparency.Measurements.csproj -c Release -- --compare artifacts/lucid-tile.json --rgba8 --tiles-only
```

原生参考构建依赖 Windows、Visual Studio C++ 工具链、Clang、CMake、Ninja 和 Vulkan 驱动。复现工具在独立临时目录构建作者固定版本，依次运行 depth filter 3、8、16，保存完整设置与缓存几何两组计时，并在结束时清理构建目录。参考驱动及补丁遵循作者的 GNU GPL v3；它们作为独立比较工具构建。

指定几何名称可选择对应的输入。保持球体形状与深度位置，只改变光照颜色的对照命令为：

```powershell
& 'F:/SomeEngine.Next/tools/Invoke-Benchmark.ps1' dotnet run --project tools/SomeEngine.Transparency.Measurements/SomeEngine.Transparency.Measurements.csproj -c Release -- --compare artifacts/material-comparison.json spheres "shaded separated spheres"
```

PPLL 节点池按硬件光栅统计的完整覆盖量分配。峰值覆盖数在 32 以内时采用局部插入排序，较大覆盖量采用链表归并排序，合成遍历全部片元。四阶、六阶 moment 重建函数来自 [作者的 CC0 shader 代码](https://momentsingraphics.de/I3D2018.html)，保留于 `Shaders/moment_reconstruction.slang`。权函数与精度配置见对应 shader 及测量 JSON。

DefaultGame 在 opaque visibility 着色之后、TAA 之前安装持久颜色 pass。研究场景由 8 个内部随法线光照变化的交叠球体网格组成，alpha 为 0.5、模型深度间隔为 0.025；每个球体包含 168 个三角形。场景几何在初始化时形成，帧内执行覆盖查询与合成。

```powershell
& 'F:/SomeEngine.Next/tools/Invoke-Benchmark.ps1' dotnet publish templates/DefaultProject/Game.App/Game.App.csproj -c Release -o templates/DefaultProject/Game.App/bin/Release/net10.0/publish
& 'F:/SomeEngine.Next/tools/Invoke-Benchmark.ps1' pwsh -NoProfile -File tools/Measure-Scenes.ps1
& 'F:/SomeEngine.Next/tools/Invoke-Benchmark.ps1' pwsh -NoProfile -File tools/Measure-Scenes.ps1 -ApplicationArguments '--transparency-research'
& 'F:/SomeEngine.Next/tools/Invoke-Benchmark.ps1' pwsh -NoProfile -File tools/Measure-Scenes.ps1 -ApplicationArguments '--transparency-tiles-hardware'
& 'F:/SomeEngine.Next/tools/Invoke-Benchmark.ps1' pwsh -NoProfile -File tools/Measure-Scenes.ps1 -ApplicationArguments '--transparency-tiles-software'
```

行为检查通过公共 C# API 比较排序合成颜色，并验证改变输入顺序后的颜色：

```powershell
& 'F:/SomeEngine.Next/tools/Invoke-Benchmark.ps1' dotnet test tests/SomeEngine.Render.Tests/SomeEngine.Render.Tests.csproj -c Release --filter FullyQualifiedName~TransparencyBehaviorTests
```

## Forward+ cluster 的 X-ray 与贴花实验

四条路径共用投影几何、材质函数、opaque 深度和 FP16 场景颜色：硬件前向、二维 tile、每帧刷新深度索引的 XYZ cluster、复用静态深度索引的 XYZ cluster。X-ray 与贴花的硬件前向开启只读 early depth，并继续采样同一张深度图执行效果区间条件。材质包含 64×64 图案、插值法线、漫反射和高光；贴花执行带图案和边缘淡出的颜色混合。

```powershell
& 'F:/SomeEngine.Next/tools/Invoke-Benchmark.ps1' dotnet run --project tools/SomeEngine.Transparency.Measurements/SomeEngine.Transparency.Measurements.csproj -c Release -- --blend-effects artifacts/transparency-effects.json
& 'F:/SomeEngine.Next/tools/Invoke-Benchmark.ps1' dotnet run --project tools/SomeEngine.Transparency.Measurements/SomeEngine.Transparency.Measurements.csproj -c Release -- --blend-effects artifacts/blend-validation.json --validate "Xray panels 32" "Xray spheres 8" "Decals 128"
```

`--validate` 开启统一 RHI validation 和原生验证入口，执行图像及重复帧检查。性能模式按每条路径的 GPU 估算时间设置目标 250 ms 的连续预热帧数，再采样 24 帧，GPU 时间包含每帧深度索引刷新、屏障、覆盖、材质和混合。静态投影几何与 CPU 二维引用构建发生在初始化阶段，耗时单列于结果。

```powershell
& 'F:/SomeEngine.Next/tools/Invoke-Benchmark.ps1' pwsh -NoProfile -Command "& 'tools/Measure-Scenes.ps1' -ApplicationArguments @('--blend-effect=Xray','--blend-method=Forward')"
& 'F:/SomeEngine.Next/tools/Invoke-Benchmark.ps1' pwsh -NoProfile -Command "& 'tools/Measure-Scenes.ps1' -ApplicationArguments @('--blend-effect=Xray','--blend-method=Cluster')"
& 'F:/SomeEngine.Next/tools/Invoke-Benchmark.ps1' pwsh -NoProfile -Command "& 'tools/Measure-Scenes.ps1' -ApplicationArguments @('--blend-effect=Decals','--blend-method=Forward')"
& 'F:/SomeEngine.Next/tools/Invoke-Benchmark.ps1' pwsh -NoProfile -Command "& 'tools/Measure-Scenes.ps1' -ApplicationArguments @('--blend-effect=Decals','--blend-method=Cluster')"
```

`--blend-method` 同时支持 `Tile` 和 `ClusterCached`。统一场景分别使用 32 个 X-ray 面板和 128 个颜色贴花，接在 opaque visibility 着色之后、TAA 之前；三场景继续提供各自实际 opaque 深度。深度索引每图元 4 B，wave 先归约实际 Z 占用位集，再以 ballot 生成完整候选掩码，resolver 的像素状态保存在寄存器。32 个 Z 分区控制空间分辨率，处理数量由实际图元决定。实测表、总显存和图像误差见 [实验记录](../../wiki/architecture/rendering/Transparency-Tile-Visibility.md#x-ray-与贴花的-cluster-实测)。

## 效果专项 benchmark

`BlendWorkloads` 集中定义机械部件遮挡、重叠贴花和稀疏贴花。隔离测量与 DefaultGame 共用这组几何、数量及深度范围。`--blend-scene=Assembly|DecalLayers|SparseDecals` 选择效果输入，`--blend-method=Forward|Tile|Cluster|ClusterCached` 选择执行方式。OIT 使用 `--oit-scene=glass|smoke|foliage`，配合 `--transparency-tiles-software` 或 `--transparency-tiles-hardware` 选择光栅入口。

```powershell
& 'F:/SomeEngine.Next/tools/Invoke-Benchmark.ps1' dotnet run --project tools/SomeEngine.Transparency.Measurements/SomeEngine.Transparency.Measurements.csproj -c Release -- --compare artifacts/oit-workloads.json 'intersecting glass' 'smoke clouds' 'foliage patches'
& 'F:/SomeEngine.Next/tools/Invoke-Benchmark.ps1' dotnet run --project tools/SomeEngine.Transparency.Measurements/SomeEngine.Transparency.Measurements.csproj -c Release -- --blend-effects artifacts/effect-workloads.json 'Xray assembly 64' 'Decal layers 256' 'Sparse decals 64'
```

`--blend-reference=<shader 路径>` 在同一进程中加入原版 tile 和 XYZ shader。结果同时保存两份 resolver 源码、图像误差、受影响像素、引用数、显存和预热设置。`--blend-shader=<shader 路径>` 在 DefaultGame 中选择对应参考 resolver。参考路径通过现有 shader 构建入口使用相应深度数据表示。

统一场景采用相同 NativeAOT 应用和已构建内容。`Measure-Transparency.ps1` 通过 `Measure-Scenes.ps1` 顺序测量机械 X-ray、重叠贴花及交错玻璃，保存整帧与逐帧数据。三个场景的采样起点分别为 2048、1024、2048，每个场景采样 256 帧。工具在结束时恢复应用原有场景配置。

```powershell
$effectApplication = 'templates/DefaultProject/Game.App/bin/Release/net10.0/publish/Game.App.exe'
& 'F:/SomeEngine.Next/tools/Invoke-Benchmark.ps1' pwsh -NoProfile -File tools/Measure-Transparency.ps1 -ApplicationPath $effectApplication
```

传入 `-ReferenceShaderPath` 可同时测量原版 XYZ，得到八组完整三场景反馈。原版 resolver 可以从 `fa94e04e` 的同名 shader 导出；临时参考文件在使用后删除。本轮内容输入复用渲染性能分支已经构建的 1024 小球、Sponza 与 Bistro 数据。

正式 Cluster 八效果使用 `Measure-ClusterEffects.ps1`，各场景保留完整预热与收敛要求后采集固定的 64 帧。Sponza 对效果默认首帧达到 768 的组从第 384 帧采样，其余场景沿用各效果首帧；同一场景的 XY/XYZ 使用相同窗口。`-FirstFrames` 显式设置优先，实际窗口记录在结果的 `SceneSettings`，默认首帧表见[统一场景指南](../../wiki/architecture/rendering/Cluster-Blend-Integration.md)。

```powershell
& 'F:/SomeEngine.Next/tools/Invoke-Benchmark.ps1' pwsh -NoProfile -File tools/Measure-ClusterEffects.ps1 -ApplicationPath $effectApplication
```
