# 三场景性能恢复

规格：[#190](https://github.com/ODtian/SomeEngine.Next/issues/190)。统一反馈票为 [#81](https://github.com/ODtian/SomeEngine.Next/issues/81)，本轮交付为 #189/#201。

当前目标（2026-10-11 更新）：1024 运动小球、Sponza、Amazon Bistro 的整帧 P95 均须严格小于 1ms（`P95Ms < 1`）；等于 1ms 不达标。沿用原场景、分辨率、质量与 presented-frame elapsed 计时口径。

`tools/Measure-Scenes.ps1` 与性能报告只检查采样完整性并报告数值，没有 P95 阈值判定。`Complete=true`、三景 `Measured` 表示反馈完整，不表示达到上述目标。

## 2026-10-13 固定帧窗口反馈

本轮基线为 `0c1048f8`，集成源码为 `869fc2e5`。三份内容均从原始来源更新；对比使用
RTX 3080、1280×720、NativeAOT/Satori、Forward、固定 1/60 秒步长及原有 TAA/VSM/async 设置。
1024 小球保持运动，Sponza 与 Amazon Bistro 保持原静态场景。每景均指定
`MeasurementFrames = { FirstFrame: 2400, FrameCount: 1200 }`，实际完整采样 2400–3599 帧。

基线与集成版分别通过 `tools/Measure-Scenes.ps1 -ShadingPath forward` 完成反馈，包含启动共
38.73 秒与 37.56 秒。两轮均 `Complete=true`、三景 `Measured`，`TraceEnabled=false`。

| 场景 | 基线均值 ms | 集成均值 ms | 基线 P95 ms | 集成 P95 ms |
| --- | ---: | ---: | ---: | ---: |
| 1024 运动小球 | 2.17782 | 2.01601 | 2.9180 | 2.2368 |
| Sponza | 2.26854 | 2.07088 | 3.0888 | 2.2554 |
| Amazon Bistro | 2.73273 | 2.47934 | 3.6892 | 2.7649 |

本轮提交将流式页字节数和读回 pass 索引移到显式变更时维护，展开需求直接写入预留缓冲，
边界状态集中保存并只更新完成点，Forward 跳过零表面数据步长下的原子计数。
对应提交为 `5021318d`、`53b0217e`、`269e29cf`、`869fc2e5`。
Bistro 的 Prepare 均值从 0.364689 ms 降到 0.308432 ms；整帧结果按上述合并版本记录。

三景驻留页分别为 5/5、83/83、2673/2722；queued/active loads、backpressure、dropped faults
和阴影分辨率偏置均为零。验证沿公共行为逐步完成：持久读回 4 项、集成流送与移动镜头阴影
6 项、依赖展开 3 项、边界状态提交 6 项、两条着色路径切换 1 项均通过，默认行为规则检查通过。
原版与着色候选的对应颜色图逐字节一致；入口切换按 Half 输出与位置插值精度比较。
NativeAOT 集成构建成功，本轮测量与验证结论集中保存于本节。

## 历史反馈

以下日期与提交记录保留当时的 1/2/3ms 目标、构建配置及测量结果，不代表当前目标；历史报告不改写。

2026-10-09 当前源码 `e13e48627650e9c7b8afbb2ac6fee109b97db6f0` 合并层级遍历与 cluster 剔除，四阶段各一次持久 kernel。原完整几何、中断恢复、容量不足公开行为 3/3 通过。原三场景 `tools/Measure-Scenes.ps1` 无 trace 反馈共 39.73 秒，框架采样 26.8280756 秒，Complete=true、3/3 Measured。

| 当前场景 | P50（ms） | P95（ms） | 采样帧 | P95 目标（ms） |
| --- | ---: | ---: | ---: | ---: |
| 1024 运动小球 | 1.8279 | 2.0078 | 1093 | 1 |
| Sponza | 3.3307 | 3.5152 | 601 | 2 |
| Amazon Bistro | 3.0889 | 3.3776 | 648 | 3 |

三景 queued/active loads、backpressure、dropped faults 均为 0；1280×720、JIT=false、原运动/光源、TAA/async/mesh、4+8 查询与 8 texels 保持原配置，1536 项几何身份相同。相对上一轮 `5a29d2fc` 的 P95 2.0630/2.8346/3.5176ms，本轮变化为 -0.0552/+0.6806/-0.1400ms。继续修复 Sponza 合并队列回退与现有 GPU 热点，1/2/3ms 目标持续验收。

当次证据：`F:/SomeEngine.AgentWork/rg70-delivery-evidence/final/reduce-render-cost-190/persistent-normal-feedback/results.json`、`persistent-normal-identity.json`、`persistent-normal-application-files.json`。[#190 当次反馈](https://github.com/ODtian/SomeEngine.Next/issues/190#issuecomment-6072397492)。以下保留前次来源记录。
2026-10-09 本批源码 `594c66efd4621671517c2fec179c6b4beda42220` 修复材质核心颜色写入归属和通用队列选择：材质返回当前像素颜色，管线写入唯一坐标；自动 copy 队列在前驱传输成本后计入显式消费队列。公开颜色行为 2/2、队列行为 8/8 通过。复用原三景 QEM 几何与原配置，通过 `tools/Measure-Scenes.ps1` 执行一次统一反馈；测量期间两个阴影实现代理暂停重 CPU 工作。报告 `Complete=true`，三景均 `Measured`，框架计时 26.1214475 秒，脚本含启动 33.71 秒。

| 当前场景 | P50（ms） | P95（ms） | 采样帧 | P95 目标（ms） |
| --- | ---: | ---: | ---: | ---: |
| 1024 运动小球 | 1.8699 | 2.0529 | 1063 | 1 |
| Sponza | 2.6415 | 2.8549 | 756 | 2 |
| Amazon Bistro | 3.2019 | 3.5558 | 617 | 3 |

三景 queued/active loads、backpressure、dropped faults 均为 0，按框架连续 ready 条件完成收敛；驻留页分别为 7/7、98/98、2541/2972。配置保持原 1280×720、TieredCompilation=false、固定 1/60、3 秒预热、2 秒采样、原运动/光源、TAA/async compute/mesh shader、4+8 查询及 8 texels 配置；1536 项网格 SHA256 与正式 QEM 内容相同。原始反馈位于 `F:/SomeEngine.AgentWork/rg70-delivery-evidence/final/reduce-render-cost-190/queue-feedback/results.json`，源码与 App 身份位于同目录上级的 `queue-identity.json`、`queue-application-files.json`。前次 `a8225b96` 的 P95 为 2.6202 / 3.2384 / 4.3895ms，其完整来源及 2393 项资产身份保存在 `F:/SomeEngine.AgentWork/rg70-delivery-evidence/final/cluster-189/qem-assets-194/feedback/`。1/2/3ms 性能目标继续推进，RT 精度修复继续由 #189 验收。
当前 #190 继续实现，#189 的 RT GT 继续验收。下表属于 `e3a97e66` 的旧提交记录；随后 RT GT 定位到该提交的 Sponza 七层几何只遍历六层，主体接收面覆盖为 18.55%。集成 `98f70878` 已恢复完整遍历，修复阴影深度精度并持久化材质资源连接。后续统一性能反馈采用修复后的完整几何与正确阴影结果。

当次源码 `e3a97e66c1c78dea5f4136f6d12393a1e52207ad`，三场景均 Complete/Measured，非 trace 原始采样 26.2796009 秒，含启动整轮 34.21 秒。

| 场景 | P95（ms） | P50（ms） |
| --- | ---: | ---: |
| 1024 运动小球 | 5.3710 | 4.6609 |
| Sponza | 3.4827 | 2.0072 |
| Amazon Bistro | 6.3762 | 5.5435 |

报告使用框架 presented-frame elapsed 与 CPU 阶段计时。三景 queued/active loads、backpressure 和 dropped faults 均为 0；驻留页分别为 7/7、103/103、2586/2996，Bistro 按当前可见需求完成流送。

配置为 1280×720、RTX 3080（driver 32.0.16.1074）、.NET 10.0.12、TieredCompilation=false、固定 1/60、3 秒预热及 2 秒采样、TAA/async compute/mesh shader 开启，采用原几何与运动。软采样每 family 最多 4 次遮挡查询与 8 次过滤，最大半径 8 texels；SMRT 搁置，三景 distant prefilter 关闭，几何预算 256MiB。

使用 `tools/Measure-Scenes.ps1` 完成当次 ≤60 秒的三景反馈，内容构建另计。原始 `results.json`、`identity.json`、应用与 2402 项内容文件身份、Apps/Library 保留于 `F:/SomeEngine.AgentWork/rg70-delivery-evidence/final/cluster-189/ticket-201/`。

前次源码 `51693b01ad9d2c55d50a4891d427f3788df890b2` 的 P95 4.2405 / 6.0294 / 7.5178ms 作为历史基准。#190 继续按 1/2/3ms 目标推进。

首轮反馈在 Bistro 初始化阶段达到脚本时限，其日志独立保留。随后单 Bistro 框架 trace 将热点定位到流送 fixup 的碎片状态区间：StateUpdateConstruction 13.829s、ResourceHazards 1.995s。合并 cluster header 访问及 240B 层级节点上传区间后完成上表反馈；诊断 trace 位于 `ticket-201/diagnostic/`，性能数据取上表非 trace 结果。
