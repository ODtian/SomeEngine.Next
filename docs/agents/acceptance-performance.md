# 三场景性能恢复

规格：[#190](https://github.com/ODtian/SomeEngine.Next/issues/190)。统一反馈票为 [#81](https://github.com/ODtian/SomeEngine.Next/issues/81)，本轮交付为 #189/#201。

当前目标（2026-10-11 更新）：1024 运动小球、Sponza、Amazon Bistro 的整帧 P95 均须严格小于 1ms（`P95Ms < 1`）；等于 1ms 不达标。沿用原场景、分辨率、质量与 presented-frame elapsed 计时口径。

`tools/Measure-Scenes.ps1` 与性能报告只检查采样完整性并报告数值，没有 P95 阈值判定。`Complete=true`、三景 `Measured` 表示反馈完整，不表示达到上述目标。

## 2026-10-13 阶段反馈与基线核对

既定产品基线为 `3788fbd8`，渲染实现为 `a0c84cd8`。冻结记录位于
`F:/AI-Workspaces/session-3884061aa3d25950/evidence/performance/product-baseline-3788-20261012/result.json`。
配置为 RTX 3080、1280×720、NativeAOT/Satori、Forward、固定 1/60 秒步长及原有 TAA/VSM/async 设置。
1024 小球保持运动，指定第 2048–3071 帧；Sponza 与 Amazon Bistro 保持静态，采用原三秒热身、两秒采样。

| 既定基线场景 | 均值 ms | P95 ms | 采样帧数 |
| --- | ---: | ---: | ---: |
| 1024 运动小球 | 2.01020 | 2.1575 | 1024 |
| Sponza | 2.05374 | 2.2109 | 973 |
| Amazon Bistro | 2.50746 | 2.7462 | 798 |

本轮曾将 `0c1048f8` 的临时重测值列作基线，并将三景采样统一改为第 2400–3599 帧。
原 23%–27% 降幅表述撤回；该轮保留为局部实验。集成源码 `869fc2e5` 在该窗口的绝对结果如下，
包含启动共 37.56 秒，三景各 1200 帧，`Complete=true`、`Measured`、`TraceEnabled=false`。
后续收益比较沿既定产品基线的源码、内容与采样配置进行，验收完成条件为三景整帧 P95 均严格小于 1 ms。

| 本轮阶段采样场景 | 均值 ms | P95 ms |
| --- | ---: | ---: |
| 1024 运动小球 | 2.01601 | 2.2368 |
| Sponza | 2.07088 | 2.2554 |
| Amazon Bistro | 2.47934 | 2.7649 |

本轮提交将流式页字节数和读回 pass 索引移到显式变更时维护，展开需求直接写入预留缓冲，
边界状态集中保存并只更新完成点，Forward 跳过零表面数据步长下的原子计数。
对应提交为 `5021318d`、`53b0217e`、`269e29cf`、`869fc2e5`。
Bistro 的 Prepare 均值为 0.308432 ms；整帧结果按上述合并版本和独立采样窗口记录。

三景驻留页分别为 5/5、83/83、2673/2722；queued/active loads、backpressure、dropped faults
和阴影分辨率偏置均为零。验证沿公共行为逐步完成：持久读回 4 项、集成流送与移动镜头阴影
6 项、依赖展开 3 项、边界状态提交 6 项、两条着色路径切换 1 项均通过，默认行为规则检查通过。
原版与着色候选的对应颜色图逐字节一致；入口切换按 Half 输出与位置插值精度比较。
NativeAOT 集成构建成功，本轮测量与验证结论集中保存于本节。

### 2026-10-13 队列与提交路径反馈

源码 `48394edc` 集成按提交批次维护命令与完成点、按页编号维护驻留链、原位三角形裁剪，
以及 GPU worker 分组领取遍历范围。各组保留已领取的 node 范围和 cluster 批次位置，等待对应生产者发布后消费。
首帧完整层级与容量不足两项公共行为检查通过，默认测试规则检查通过。

沿既定产品基线配置执行 `tools/Measure-Scenes.ps1 -ShadingPath forward`，包含启动共 37.89 秒；
NativeAOT/Satori、1280×720、Forward、原 TAA/VSM/async 设置，三景均 `Measured`、`Complete=true`、
`TraceEnabled=false`。运动小球仍采第 2048–3071 帧，静态场景仍采用三秒热身、两秒采样。

| 场景 | 均值 ms | P95 ms | Prepare 均值 ms | 采样帧数 |
| --- | ---: | ---: | ---: | ---: |
| 1024 运动小球 | 1.825141 | 2.1456 | 0.192574 | 1024 |
| Sponza | 2.050635 | 2.2366 | 0.092251 | 975 |
| Amazon Bistro | 2.418424 | 2.6125 | 0.201011 | 827 |

三景驻留页为 5/5、83/83、2673/2722，queued/active loads、backpressure、dropped faults 与阴影分辨率偏置均为零。
这些数据记录本轮组合改动的整帧反馈；既定产品基线与三景 P95 严格小于 1 ms 的验收条件继续沿用上文。

### 2026-10-13 材质合并与等待收集反馈

源码 `438034c7`，加 `b5a94fb1` 的接收面梯度复用。该组合按材质索引读取遮罩纹理、
按显式材质变化维护光栅资源依赖、从稀疏资源子集收集描述符引用，并按缓冲区完成点来源收集等待。
遮罩纹理替换 1 项、描述符复用 2 项、缓冲区等待与状态交接 7 项公共行为检查通过，默认行为规则检查通过。
软阴影采样复用当前接收面的深度梯度，粗层斜面采样行为检查通过。

执行 `tools/Measure-Scenes.ps1 -ShadingPath forward -ScenePlayback static`，包含启动共 34.45 秒。
配置沿上文冻结基线：运动小球为第 2048–3071 帧，静态场景为三秒热身、两秒采样，
1280×720、NativeAOT/Satori、原 TAA/VSM/async 设置。三景均 `Measured`、`Complete=true`、`TraceEnabled=false`。

| 场景 | 均值 ms | P95 ms | Prepare 均值 ms | GraphExecute 均值 ms | 采样帧数 |
| --- | ---: | ---: | ---: | ---: | ---: |
| 1024 运动小球 | 1.757463 | 1.9590 | 0.178714 | 0.503426 | 1024 |
| Sponza | 2.012012 | 2.2581 | 0.097765 | 0.463243 | 994 |
| Amazon Bistro | 2.377901 | 2.5855 | 0.204511 | 0.597574 | 841 |

三景驻留页为 5/5、83/83、2673/2722，queued/active loads、backpressure、dropped faults 与阴影分辨率偏置均为零。
后续继续沿三景 P95 严格小于 1 ms 的目标优化提交与 GPU 工作。

### 2026-10-13 深度分桶与单队列提交反馈

源码 `94d193d4`，加遍历就绪标记在消费时清除的同步候选。包含按 wave 并行预留深度桶、
按下游约束选择兼容队列，以及 node/cluster 消费时的组内同步整理。
深度桶顺序 1 项、队列交接 5 项、首帧完整层级和空帧恢复 2 项公共行为检查通过，默认行为规则检查通过。

执行 `tools/Measure-Scenes.ps1 -ShadingPath forward -ScenePlayback static`，包含启动共 31.18 秒。
冻结基线配置继续采用 1280×720、NativeAOT/Satori、原 TAA/VSM/async 设置，
运动小球采第 2048–3071 帧，静态场景采用三秒热身、两秒采样。
三景均 `Measured`、`Complete=true`、`TraceEnabled=false`。

| 场景 | 均值 ms | P95 ms | Prepare 均值 ms | GraphExecute 均值 ms | 采样帧数 |
| --- | ---: | ---: | ---: | ---: | ---: |
| 1024 运动小球 | 1.881145 | 2.0540 | 0.188961 | 0.478302 | 1024 |
| Sponza | 2.164300 | 2.3444 | 0.095683 | 0.451557 | 924 |
| Amazon Bistro | 2.357257 | 2.6139 | 0.245079 | 0.634763 | 848 |

三景驻留页为 5/5、83/83、2673/2722，queued/active loads、backpressure、dropped faults 与阴影分辨率偏置均为零。
本轮组合的 P95 高于上一轮，下一步按 Forward GPU 着色耗时和队列间并行关系继续改进。
三景 P95 严格小于 1 ms 的验收条件继续沿用。

### 2026-10-13 Bistro GPU 定位

当前采集应用为 `94d193d4` 加 BVH ready 标志清理与同步修改，包含原位裁剪、索引遮罩纹理、
wave 深度桶前缀和消费者队列传播。对照为 `3788fbd8` / `a0c84cd8` 的已有八帧 GPU 定位。
两次均使用 RTX 3080、NativeAOT/Satori、1280×720、静态 Bistro、Forward、固定 1/60 秒步长，
原 TAA/VSM 配置，在第 1000 次 Present 后采八帧；Nsight Graphics 2026.2 GPU Trace Profiler，
100ms 上限、unaltered clocks、每 action 时间戳及实时 shader PC 采样。
当前单次采集用时 42.32 秒，结束时 meshes 为 1532/1532、pages 为 2673/2722，queued/active/failures 均为零。
同版正式反馈在第 106 帧完成流送，Bistro 正式采样窗口为第 668–1515 帧。

表中时间是八帧的增量 action 均值，用于定位管线成本；整帧验收继续采用上文统一平台的无 trace 数据。
当前帧时钟与 Nsight 的 `FRAME.xls` 导出逐帧相差至多 4ns。

| 管线范围 | 既有定位 ms | 当前定位 ms | 当前每帧 action 数 |
| --- | ---: | ---: | ---: |
| 主 Forward PSO | 0.710376 | 0.717376 | 1 |
| 全部 Forward | 0.770032 | 0.777136 | 7 |
| 主视图 BVH | 0.258272 | 0.201800 | 2 |
| 主 SW raster PSO | 0.280868 | 0.267768 | 2 |
| 主视图全部 SW raster | 0.323604 | 0.296088 | 14 |
| 主视图全部 HW raster | 0.191412 | 0.209384 | 14 |
| 阴影全部 SW raster | 0.015080 | 0.009204 | 14 |
| 阴影全部 HW raster | 0.004316 | 0.004616 | 14 |
| Raster bin reserve | 0.073380 | 0.020176 | 4 |
| 全部记录的 draw/dispatch action | 2.375400 | 2.258104 | 125 |

当前主视图与阴影各有七个 SW、七个 HW 材质管线，每个管线在两个阶段各执行一次，
因此四类 raster action 均为每帧 14 次。这直接确认每阶段七个材质 bin。
既有定位中四类 raster action 均为每帧 26 次，对应每阶段十三个材质 bin。
当前八帧均由一次 Graphics `ExecuteCommandLists` 提交八个 command list；
既有八帧每帧为 Graphics 八个列表与 Compute 一个列表两次提交。

| 驱动 shader 数据 | 既有寄存器/线程 | 当前寄存器/线程 | 既有/当前静态 shared 字节 | 既有/当前代码字节 |
| --- | ---: | ---: | ---: | ---: |
| 主 SW raster，32线程/组 | 168 | 128 | 3584 / 3072 | 110592 / 96128 |
| Raster bin reserve，128线程/组 | 37 | 24 | 0 / 0 | 9216 / 5120 |
| Forward，64线程/组 | 128 | 128 | 5888 / 5888 | 506496 / 497408 |
| 主视图 BVH，64线程/组 | 163 | 163 | 512 / 512 | 86400 / 91648 |

当前七个 Forward 材质管线共享一份驱动 shader。全记录 471370 个原始 PC 样本中，
198674 个归属于 Forward（42.15%），主视图 BVH 为 10.16%，主 SW raster 为 9.08%；
42472 个样本位于记录的 shader 地址区间之外。该比例描述采样分布。

八帧原始时间保留如下。当前第零帧 Forward 为 0.930944ms，其余七帧为 0.681440–0.699936ms；
上述均值包括全部八帧。寄存器数据来自当次报告的 shader 元数据，按报告内 function-group 名称对应。

| 帧序号 | 既有帧间隔 ms | 当前帧间隔 ms | 既有主 Forward ms | 当前主 Forward ms |
| --- | ---: | ---: | ---: | ---: |
| 0 | 2.580544 | 2.866144 | 0.707584 | 0.930944 |
| 1 | 3.166944 | 2.441856 | 0.717728 | 0.692544 |
| 2 | 2.663872 | 2.531168 | 0.706464 | 0.699936 |
| 3 | 2.618432 | 2.423648 | 0.714080 | 0.681856 |
| 4 | 2.592160 | 2.443552 | 0.712064 | 0.683296 |
| 5 | 2.691680 | 2.480576 | 0.707040 | 0.685184 |
| 6 | 2.580640 | 2.394752 | 0.708640 | 0.683808 |
| 7 | 2.577792 | 2.425664 | 0.709408 | 0.681440 |

本次定位结论集中于本节，使用后的采集文件、解析脚本与日志已清理。

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
