# 阴影独立参考验收 — 2026-10-04

任务：[#125](https://github.com/ODtian/SomeEngine.Next/issues/125)，包含原 #124 的参考验证职责。
本页保存 `cbb28b28` 阶段的修复、当时筛选条件下的三场景 RT 比较与相关后端验收。

## #127 对原记录的复核

原 RT 比较使用着色法线选择接收者并沿着色法线偏移射线起点。呈现图的红通道
经 128 阈值二值化后参与比较；5×5 邻域同时要求参考可见性一致与生产接收者有效。
下文“最终通过”表属于这个筛选范围。原 CSV 与 `shadow-error.bmp` 保存该筛选范围内的差异。

对保存的原图重算，得到全部共同有效接收面上的范围：

| 场景 | 共同有效像素 | 差异像素 | 差异比例 | 原筛选保留比例 |
| --- | ---: | ---: | ---: | ---: |
| 1024 小球 | 113,921 | 2,860 | 2.5105% | 25.0577% |
| Sponza | 439,028 | 453 | 0.1032% | 65.1535% |
| Amazon Bistro | 629,836 | 33,407 | 5.3041% | 35.1885% |

小球 1,415 个、Bistro 4,675 个差异仅因邻域接收者资格被筛出；其余筛选原因和原图
SHA-256 记录在 `ticket-127.json`、`ticket-127-evidence/before-mask-statistics.json` 与
`before-mask-exclusion-reasons.json`。本次统一修复验收使用显式测试材质、独立几何射线、
连续可见性与全量差异；分别统计边界、内部、接收面覆盖及着色条件。

## 源码与证据

- 基线：`f1a8dcf8d03c865f010e041cb5dbb77058056eb1`。
- 场景捕获前置提交：`19a0754cbc49ef81834017034c703533d02592ec`。
- 交付分支：`codex/rg70-ticket-125`。
- 工作树：`F:\CodexProjects\2026-10-03\new-chat-2\work\rg70\trees\ticket-125`。
- 证据根目录：`F:\CodexProjects\2026-10-03\new-chat-2\work\rg70\ticket-125-evidence`。
- 修复提交、命令与图像路径：`F:\CodexProjects\2026-10-03\new-chat-2\work\rg70\ticket-125.json`。

各 `comparison.json` 的 `SourceCommit` 记录执行时 HEAD；本次验收执行包含随后提交的修复。
交付 JSON 将这些执行证据关联到修复提交。下文路径相对于证据根目录。

## 自动测试与真值

`DefaultGameRayShadowTests.ThreeSceneShadowsMatchHardwareRayTracing` 的三个 theory case
通过普通 `dotnet test` 发现并执行。场景为 1024 小球、Sponza 和 Amazon Bistro。
测试通过 DefaultGame 的 `--test-scene` 入口读取场景资产，并取得该场景的相机与方向光。
测试材质保留自身的顶点解释、法线与透明度处理，输出 VSM 可见性。

真值由硬件 DXR inline `RayQuery` 产生：原始 glTF 三角形与实例变换构成 BLAS/TLAS；
相机射线选择可见接收面，向光源的遮挡射线判断阴影。参考保留源法线、切线空间、
法线纹理与 bilinear-repeat alpha-mask 交点判定。测试使用普通 RHI 公共 API 构建和追踪。
`AccelerationStructure.DeviceAddress` 提供 TLAS 实例所需的 GPU 地址。

比较条件：1280×720，D3D12 RTX 硬件，静止场景，64 帧，固定步长 1/60 s，VS/PS，
TAA=false，async compute=false，同步帧，隐藏窗口与调试 UI。
原接收者按法线纹理参与后的着色法线选择，朝向相机并朝向光源的余弦至少为 0.15。
原 5×5 邻域筛选要求参考可见性一致，并要求全部邻居具备生产接收者资格；至少比较 512 个像素。
总差异、误阴影占参考受光像素比例、漏阴影占参考阴影像素比例，分别要求 ≤2%。
阈值、边缘处理与参考算法在接触阴影修复前后保持一致。

每个场景保存 `scene.bmp`、`vsm-shadow.bmp`、`rt-shadow.bmp`、`shadow-error.bmp`、
`ray-comparison.csv`、`comparison.json` 及捕获进程的实际命令和输出。
另有 7 项 CPU 原始三角形射线回归，覆盖方向光、局部光、运动遮挡者、接触阴影、
软件光栅与 1024 小球布局；18 项渲染行为回归覆盖 D3D12/Vulkan、硬件/混合光栅、
mesh shader、光源移动、相机滚动、法线偏移和兜底阴影更新。

## 根因与修复

1. 方向光页请求按像素投影选取 clipmap；材质采样进一步寻找覆盖接收面的 clipmap。
   两者选择差异造成实际采样投影缺页。`SelectDirectionalVirtualShadowView` 统一选择，
   将页池压力偏移施加于覆盖接收面的投影；DefaultGame 启用 VSM 分辨率反馈。
2. 移动遮挡者只使方向光 mip 0 失效，最粗兜底 mip 保留移动前的深度。
   旧、新包围盒现在刷新该投影的全部缓存 mip。
3. 原默认 `DepthBias=0.0005` 在默认方向光深度范围中约为 0.5 m，削弱 Bistro 接触阴影。
   采样改为在实际阴影 texel 中心计算接收面深度，硬比较和过滤共用接收面梯度。
   默认余量调整为 `0.00002`，约 2 cm；默认世界空间法线偏移为 0.01。
4. 帧图像回读明确使用 UI 区段，与呈现图像共用对应提交。

生产数据继续通过 GameWorld 提取到 RenderingWorld；材质 shader 解释自身顶点数据。
VSM 持有页状态和分辨率反馈，持久图沿已有区段维护。

## 修复前失败证据

| 回归 | 比较像素 | 漏阴影 | 误阴影 | 差异比例 | 结果 |
| --- | ---: | ---: | ---: | ---: | --- |
| Bistro 硬件 RT，接收面修复前 | 221,631 | 9,583 | 2,685 | 5.535327% | Failed |
| 移动遮挡者方向光兜底页，失效修复前 | 34,382 | 550 | 1,184 | 5.043337% | Failed |

Bistro：`rt-after/ray-shadow-bistro-90913ad1019d4753bab88659cc9922f3/`，
日志与 TRX：`rt-after/rt-three-scenes.log`、`rt-after/rt-three-scenes.trx`。
该执行已包含投影选择与兜底页失效修复，显示接触阴影问题仍可由硬件 RT 自动检出。

兜底页：`before/directional-fallback-d3d12-hardware-d2365afc1a4b4d99ab503b8e47029836/`，
`before/fallback.log`、`before/fallback.trx`。移动首帧及收敛帧与强制刷新输出的相对颜色误差
均为 `0.75027466`。该公共材质明确使用最粗 mip，可重复覆盖兜底页更新行为。

原 #124 的 1024 小球复现中，29,543 个参考受光像素出现 2,746 个误阴影，比例 9.295%。
原证据为相邻目录 `shadow-evidence/shadow-reference-directional-grid-376a47ccccc540efbda31bd72e851edc/`。

## 最终通过证据

完整 `tools/Test-Shadows.ps1` 执行发现并通过 28 项，failed=0，skipped=0，耗时 11 m 23 s。
日志：`acceptance-script.log`；发现列表：`acceptance-discovery.log`。

| 硬件 RT 场景 | 比较像素 | 参考阴影像素 | 参考受光像素 | 漏阴影 | 误阴影 | 差异比例 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 1024 小球 | 28,546 | 0 | 28,546 | 0 | 0 | 0% |
| Sponza | 286,042 | 271,847 | 14,195 | 0 | 1 | 0.000350% |
| Amazon Bistro | 221,630 | 74,662 | 146,968 | 1,363 | 2,821 | 1.887831% |

Bistro 漏阴影比例约 1.826%，误阴影比例约 1.919%，均通过 2% 门槛。
小球所选相机的比较区域覆盖参考受光面，主要检查误阴影；Sponza 和 Bistro 同时覆盖受光面与阴影面。
上述结果记录原筛选条件下的通过判定；CSV 与差异图覆盖该筛选范围。全量原图的范围
与差异见本页开头的 #127 复核。

图像与逐像素数据目录：

- `acceptance/ray-shadow-spheres-004019e6676942048ca78170024d6ab2/`
- `acceptance/ray-shadow-sponza-736863bdfec64e23b4b933dce62a9b8d/`
- `acceptance/ray-shadow-bistro-b1586ab8e85a40e2b6856c18b13a7a55/`

方向光兜底页在 D3D12/Vulkan、硬件/混合光栅四种组合中，移动首帧与收敛帧都与强制刷新
输出一致，三角形参考比较差异为 0；结果位于 `acceptance/directional-fallback-*/comparison.json`。
该用例的 128 texel 兜底投影覆盖 16 世界单位，边缘排除 5 屏幕像素，约一个粗 texel。

## RT 入口发现与相关后端

| 范围 | 发现 | 执行 | 通过 | 跳过 | 证据 |
| --- | ---: | ---: | ---: | ---: | --- |
| 三场景 RT + 阴影行为/独立参考 | 28 | 28 | 28 | 0 | `acceptance-discovery.log`、`acceptance-script.log` |
| D3D12 WARP RT 公共 API + VSM shader 行为 | 65 | 65 | 65 | 0 | `d3d12-discovery.log`、`d3d12-shadow-ray.trx` |
| Vulkan KHR RT 公共 API | 4 | 4 | 4 | 0 | `vulkan-discovery.log`、`vulkan-ray.trx` |

三场景 RT 真值本次使用 D3D12 RTX 3080；Vulkan 的记录覆盖 KHR 加速结构与 RT 管线，
Vulkan 阴影更新行为由上述完整 28 项中的相关 case 覆盖。

## 实际命令

以下命令从交付工作树执行。完整入口分别构建默认内容、场景内容、应用，再执行阴影回归。
首轮场景源准备可通过 `tools/Test-Shadows.ps1 -DownloadScenes` 完成。

```powershell
pwsh -NoProfile -File tools/Test-Shadows.ps1 -EvidenceDirectory 'F:\CodexProjects\2026-10-03\new-chat-2\work\rg70\ticket-125-evidence\acceptance'
dotnet test tests/SomeEngine.Render.Cluster.Tests -c Release --no-build --list-tests --filter 'FullyQualifiedName~DefaultGameRayShadowTests|FullyQualifiedName~DefaultGameShadowReferenceTests|FullyQualifiedName~VirtualShadowRenderingTests'
dotnet test tests/SomeEngine.Render.Cluster.Tests -c Release --no-build --filter FullyQualifiedName~MovingCasterRefreshesDirectionalFallbackShadows --logger 'trx;LogFileName=fallback.trx' --results-directory 'F:\CodexProjects\2026-10-03\new-chat-2\work\rg70\ticket-125-evidence\before'
dotnet test tests/SomeEngine.Render.Cluster.Tests -c Release --no-build --filter FullyQualifiedName~DefaultGameRayShadowTests --logger 'trx;LogFileName=rt-three-scenes.trx' --results-directory 'F:\CodexProjects\2026-10-03\new-chat-2\work\rg70\ticket-125-evidence\rt-after'
dotnet test tests/SomeEngine.Graphics.Direct3D12.Tests -c Release --no-build --filter 'FullyQualifiedName~WarpRayTracingTests|FullyQualifiedName~VirtualShadow' --logger 'trx;LogFileName=d3d12-shadow-ray.trx' --results-directory 'F:\CodexProjects\2026-10-03\new-chat-2\work\rg70\ticket-125-evidence'
dotnet test tests/SomeEngine.Graphics.Vulkan.Tests -c Release --filter FullyQualifiedName~Khr_ --logger 'trx;LogFileName=vulkan-ray.trx' --results-directory 'F:\CodexProjects\2026-10-03\new-chat-2\work\rg70\ticket-125-evidence'
pwsh -NoProfile -File tools/Measure-Scenes.ps1
```

发现 D3D12/Vulkan case 时分别以同一 filter 和 `--list-tests` 执行。
每次 DefaultGame 捕获的完整选项记录在对应场景 `scene.log` 与 `vsm-shadow.log`。
失败回归命令的执行版本与结果范围按上文修复前记录读取。

## 统一三场景反馈

硬件 NVIDIA GeForce RTX 3080，驱动 `32.0.16.1074`，.NET `10.0.12`，
TieredCompilation=false。统一平台配置为 1280×720，VSM=true，TAA=true，async compute=true，
混合光栅与 mesh shader，固定步长 1/60 s，三场景各自的默认行为。
TraceEnabled=false，诊断 trace 与此反馈分别存档。

一次 `Measure-Scenes.ps1` 反馈含启动共 39.88 s；报告记录平台运行 33.1538325 s。
Complete=true，三个场景状态均为 Measured，内容构建在该运行之前完成。

| 场景 | 帧数 | Mean ms | Median ms | P95 ms | Prepare ms | Execute ms |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 1024 小球 | 532 | 3.77 | 3.76 | 4.00 | 0.20 | 0.81 |
| Sponza | 1,108 | 1.80 | 1.76 | 2.01 | 0.08 | 0.94 |
| Amazon Bistro | 677 | 2.96 | 2.94 | 3.28 | 0.10 | 1.76 |

证据：`three-scene-feedback.json`、`three-scene-feedback.log`。
时间含义为呈现帧耗时与 CPU 阶段耗时。本次记录修复后的平台反馈。
