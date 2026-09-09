**Render enhancement：源码审查与中间表示压缩的实施记录 — 2026-09-05**

对应 [原始 UE 源码审查及中间表示 review](Render-Enhancement-UE5-Source-Audit-2026-09-05.md)。实现位于 `codex/render-enhancement` 工作树；原审查引用的 UE commit 和原始来源记录保持不变。本文描述最终实现，原报告保留为问题来源。

本次保留用户确定的 atomic64 hybrid 与 no64 全 HW 两种路径，两者都支持 tessellation。通用几何管线接受 view mask、稀疏目标区域和输出映射；VSM 页缓存的业务语义留在阴影功能中。没有增加旧格式转码缓冲、上传别名、persistent 队列兼容分支或中间微几何。

**逐项处置**

| 原审查项 | 实施结果 | 主要源码及验证 |
|---|---|---|
| 1：多 view 和逐页高层遍历 | mesh node 携带 group/mask，共享节点加载，逐 view 保留裁剪、LOD、Hi-Z 结果；空间排序后的 64 实例粗层缓存 bounds。VSM 按 source view 的 fine/coarse 层提交，cluster 可见性确定后才查询稀疏区域并展开物理页 | `cluster_bvh_traverse_*`、`cluster_scene_bounds.slang`、`cluster_cull.slang`、`cluster_raster_region_access.slang`、`vsm_view_compact.slang`；负 clipmap 原点、多页网格、上一帧投影 GPU 测试 |
| 2：固定 32 层和不安全 persistent | 按流送内容的实际 hierarchy level 构建逐层调度；删除未具备 ready 协议的 persistent 路径 | `ClusterMeshes`、`ClusterPipelineSystem.Traversal.cs`、普通 Cluster 回归。没有用跨工作组自旋替代 dispatch 同步 |
| 3：SW 通用 clip 与 bbox 空扫 | 普通 cluster 在裁剪边界路由 HW；SW trivial accept/reject 后仅对实际跨界几何裁剪；scanline 计算三条边约束的有效 x 区间 | `cluster_cull.slang`、`cluster_raster_shared.slang`、`sw_raster_core.slang`；hybrid 的 WPO/alpha 场景同时产生 HW 与 SW 工作 |
| 4：每材质 bin 六层 split | 按实际 Split pipeline 和该 entry 使用的资源绑定分组；不同 raster/alpha 资源不再无故拆开相同 split。组内 seed 后共用六层队列，final 时恢复真实 raster bin，最终只做一次 count/reserve/scatter | `ClusterMaterialStages`、`ClusterPipelineSystem.Passes.cs`、`cluster_tessellation_core.slang`；四种不同 mask 材质对照：96→24 次 split，见下方实测 |
| 5：SW 逐 row dice | group 内按唯一网格顶点合作求值，以每 lane 至多三个 float4 寄存器缓存，再并行提交微三角形；按 factor 将多个小 patch 合入一个 group | `cluster_tessellation_core.slang`、`cluster_tessellation_bin.slang`；factor 1/2/3/4/5/6/7/8 和部分 patch 组的 GPU 参数、寄存器交换与索引验证 |
| 6：HW 固定 192 VS 输入与重复 domain | 有 Mesh 时按 patch 输出唯一网格顶点和三角形；VS/PS 使用常驻拓扑 index buffer、按 factor 产生 indexed indirect draw，由硬件顶点复用处理共享顶点，删除固定 padding | 同上及 `ClusterTessellationRasterPassData`；factor 1/2/4/8 为 3/6/15/45 个唯一网格顶点，对应 3/12/48/192 个索引；HW Phong、纹理位移与多材质场景 |
| 7：逐不同 key wave matching | 支持 SM 6.5 时使用 WaveMatch；其它目标按变化的 key 位做 ballot 分区，保留完整 key 和活动 lane 语义；64 个局部顶点至多六个变化位。VSM free/eviction 分配与回收改为 wave 聚合原子 | `wave_aggregation.slang`、`vsm_allocate_pages.slang`、`vsm_page_pool.slang`；SM 6.0/6.5 分歧 lane GPU 测试及不均匀 free/eviction 尾部测试 |
| 8：未接通 async SW/dice | SW 与 dice 接入 compute queue；atomic winner 合并声明与跨队列依赖保留；去掉无消费者的 reset/冗余依赖，纹理 barrier 合并相邻层范围 | `ClusterPipelineSystem.Passes.cs`、`FrameBarrierCompaction.cs`；3 队列实际帧、异步 hazard 测试和 32 layer×3 mip 逐 texel 回读。没有将串行 replay 的 event duration 当成重叠帧时 |
| 9：VSM 原尺寸 Hi-Z 和逐 mip dispatch | 从半页尺寸开始存储；页内多个 mip 用 LDS 合并生成，尾部继续合并 reduce；保守方向与 history 保持 | `vsm_page_hiz.slang`、`vsm_page_hiz_reduce.slang`；GPU 多页各 mip 与 CPU 参考比较 |
| 10：全实例失效与缺少变形输入 | 变换抽取和实例组合只写变化行；revision 在成功发布后保留逐行变化，漏过渲染帧也不会丢失。VSM 上传变化 ID；每 caster/view 的 64 线程共同处理新旧包围盒页矩形，原子更新页标签 | `RenderExtractionSchedule.cs`、`RenderInstanceBatchBuilder.cs`、`RenderInstanceResources.cs`、`VirtualShadowFeature.cs`、`vsm_caster_invalidation.slang`；跨帧/放弃写入/共享属性/局部变化测试及实际稀疏运动抓帧 |
| 11：真实帧 CPU 解析、重复区间与上传 | 动态 upload/import/pass 更新可命中图分析复用；只在结构改变时重建分析。上传内存保持映射；resident range 共享不可变集合，访问展开仅在集合改变时重建；提交状态按 pass/buffer 批量处理，相同提交的相同读集幂等合并 | `FrameAnalysisReuse.cs`、`FrameBufferRanges.cs`、`FrameStateCommit.cs`、`BufferRangeSet.cs`、`ClusterFrameUploads`；真实默认帧复用及 CPU 基准，见下方 |
| 12：无效深度与过宽中间队列 | neutral output 缩到 1×1；work/final/index/raster/shade/VSM 条目贯穿生产、绑定和消费统一压缩；层级遍历以共享 mask 减少条数 | 下方逐格式表。声明容量与实际分配分开报告 |
| 13：tess 上传与压缩集成阻断 | seed、dice、HW 上传与实际 entry 声明统一；删除旧 `uint4` visible 消费；修复属性位流 DWORD 尾部对齐、实例元数据相对范围和未使用材质资源干扰命令分组 | Assets/Cluster 回归、fresh cook DXIL+SPIR-V、真实 hybrid/HW tessellation 帧 |
| 14：可观察验证 | 增加实际 GPU 的压缩、binning、误差界、页分配/失效、Hi-Z、view 重建测试；提供可复现场景工具和 CPU benchmark；执行功能矩阵 | 本文后两部分列出测量方法、结果和边界；没有宣称已经得到 UE 同场景帧时或厂商寄存器计数器 |
| 15：所有中间表示建议 | 无损压缩目标已贯穿实现；依赖额外引用池、改变精度或可能多算的候选按实际代价作出保留决定 | 见下表；未将候选方案全部机械替换成更小的结构体 |

**中间表示与容量**

| 表示 | 优化前 → 优化后 | 容量/语义说明 |
|---|---|---|
| split work | 24→16 B | bin 在分组/最终消费时恢复，depth 由当前层给出；保留三个完整 32-bit barycentric corner |
| final patch | 24→20 B | 保留 22-bit visible source、8-bit triangle、24-bit bin、任意 1..8 的三条 edge factor 和全部 96-bit barycentric |
| final bin | 复制 patch→4 B index | scatter 只写索引，读取时访问 final pool；索引存储复用 split scratch，不额外新建一份宽 patch pool |
| raster bin item | 8→4 B | 保存 batch ordinal，由 batch 上下文恢复 cluster/triangle range；不缩小支持的 candidate 容量 |
| shade work | 8 B/tile 或 quad | 完整 8×8 同材质 tile 一条；边界按 2×2 quad 和同材质覆盖 mask 分组；单/多 view 使用同一 linear pixel 编码 |
| traversal | 单 view 12 B→group/mask 16 B | 一个节点存活于 k 个相同组 view 时，原队列需要 12k B，现为 16 B；k=1 增加 4 B，k=32 则 384→16 B。减少条数的收益取决于实际 mask |
| candidate/visible/deferred | 保留 12 B | page 只有 16 B 对齐，page/cluster/instance/view 信息不能无损塞进 64 bit。deferred 改 4 B 索引必须延长原 candidate pool 的生命期并干扰现有复用；未增加每条唯一的上下文表来制造表面压缩 |
| VSM active page | 16→8 B | 只存 physical page 与 table index，source/coarse 可由后者推导 |
| VSM 每页 view | 432 B/page→共享 source view +48 B/region | source view 保存矩阵，region 保存 crop/输出/Hi-Z 层；grid 有额外容量，收益主要是消除高层按物理页重复工作 |
| winner | 保留 8 B/pixel | depth/id 原子一致性不变；不恢复分裂 32-bit 写入 |
| 页缓存标签、request stamp | 保留完整精度/位宽 | world page/generation 不压成 16 bit；request bitset 的 clear/代际/原子成本不当作免费压缩 |
| 微顶点、微三角形 | 没有全局中间数组 | 只保留紧凑 patch、静态拓扑索引和 group 内临时计算 |

默认 1,048,576 个 patch 容量：两个 split work 加 final 从 **72 MiB 降为 52 MiB**。默认 9 Mi 个 raster bin 条目从 **72 MiB 降为 36 MiB**。当前单 view 的 shade work 最坏容量为 1080p 15.82 MiB、4K 63.28 MiB；tile/quad 节省实际条目数量，分配仍覆盖全部像素各自不同材质的极端情况。128×128 页的单份完整 R32 Hi-Z 从 87,380→21,844 B/页；1024 页从约 85.33→21.33 MiB。以上是各命名资源的容量，**不能相加宣称整帧 GPU 峰值下降**。

新增空间缓存有真实成本：按默认 65,536 实例上限，current/previous bounds 为 4 MiB，chunk bounds 为 32 KiB。逐行 revision 为每实例 8 B 的 CPU 持久存储，chunk revision 用于跳过不变块；每次修改用位图合并多线程写入。VSM source-region 查询 grid 也有额外存储。实际帧的 `transientPhysicalBytes` 仅计图的 transient 分配，不包含以上 persistent/cache、资产、驱动和 swapchain residency。

后续已将逐像素条目替换为 tile/quad 条目。8 B quad+mask 在 1/2/3/4 个像素有效时分别需 8/4/2.67/2 B/像素；完整 8×8 tile 为 0.125 B/像素。full tile 只在全部 64 个像素有效且同材质时生成，混合/边界 quad 用 mask 屏蔽无效写入。最坏容量仍按每像素一条 8 B 分配，因此先前 4 B pixel 阶段的容量下降不能作为当前容量收益；实际写入条数的测量见文末。barycentric、winner 和 Hi-Z 精度不变。

**误差界与额外正确性修复**

纹理位移以前在 UV footprint 小于一个 texel 后仍得到不收敛的 min/max 高度范围，普通 8×8 高度图就会填满 1 Mi patch pool。现在 `TextureDisplacement` 明确使用 level 0 的硬件 bilinear-repeat 采样，调用方必须传入匹配的 linear-repeat sampler；手工 texel 读取只用于连续插值的解析导数和区间证明。高度区间结合保守纹理金字塔与 Lipschitz 收敛界，同 bilinear cell 使用双线性交叉项；法线变化及高度/法线乘积使用保守误差界。边误差以两个真实端点为基础，并计入硬件插值权重有限精度的余量。

LOD 与 tessellation 投影统一使用完整 clip matrix 的列范数、clip w 下界和误差半径，覆盖正交 VSM、透视、off-axis/crop 及实例尺度；删除按 view depth 套用透视公式到正交投影的路径。GPU 测试包含负/越界/跨 wrap UV、正负 displacement scale、逐级缩小的 336 个 patch、采样导数、正交/非中心透视误差界。保守界是数学约束，测试只是回归证据，不以有限采样代替证明。

位移场景由 1,048,576 个 final patch、327,680 个 dropped，在连续手工 bilinear 阶段降为 **16,422 个 final patch、0 dropped、192 个 quality-limited**。改用硬件过滤并计入权重精度余量后，静态场景为 **16,740 个 final patch、0 dropped、207 个 quality-limited**。quality-limited 表示达到 depth 5/最高 factor 的质量预算，没有把该指标隐藏或改成零。动态姿态的数值随运行时钟变化，不能直接拿两次动态运行做逐像素一致性比较。

还修复了两项在完整功能矩阵中发现的问题：VSM 失效读取缓存 bounds 后仍被“尚未登记本帧 BVH 范围”条件错误禁用，现已删除这个过时条件；每个 raster pass 单独 Begin/EndRendering 时，图录制器不能只因附件相同就添加 suspend/resume 标记，现已删除这种伪合并，以及已经没有执行语义的 `RasterPassMergeMode`、pass 选项和 debug 开关。相邻 clear→clear→load 的 GPU 回读验证实际附件内容。

**实测与方法**

设备为 NVIDIA GeForce RTX 3080，Windows、D3D12。GPU 帧由 RenderDoc 1.43 注入 Runtime，第 20 帧抓取；每个 capture 的 `EventGPUDuration` 重放七次。该计数器得到各事件在 replay 中的 GPU 时间，求和不是带 async overlap 的整帧墙钟时间。[RenderDoc 官方计数器示例](https://github.com/baldurk/renderdoc/blob/v1.x/docs/python_api/examples/renderdoc/fetch_counters.rst)

四材质对照使用同一场景、同一资源 GUID、同一 mask 纹理和同一代码，仅对照版本恢复“每 bin 一个 split 组”的调度。split dispatch 从 96→24；七次重放的 split 总时间中位数从 **0.419584 ms→0.108416 ms（下降 74.16%）**。同一 capture 时点的 visibility、scene depth、motion vectors 原始数据逐字节一致。最终颜色并非逐像素一致：两次运行的 1024 页缓存均已满，fine 分配失败均为 684，共有 331 个 resident fine page 被不同请求取得；共同 resident 页的 world tag/generation 一致。颜色的半精度分量最大差 0.041992、全部 RGBA 平均差约 3.12e-5，不能声称最终颜色完全相同。

曾对同一位移场景测量每 lane 深度优先 split 的独立实验：split 从约 0.20 ms 上升到 1.78 ms，因此没有采用。最终使用共享组的并行分层算法，保留六个有界依赖层；没有以更少 dispatch 为理由接受约八倍 kernel 退化。

CPU 稀疏区间基准为 64 个 reader、每 reader 128 个不相交 16 B 区间，16 次 warmup、48 次测量、四轮交替顺序；GPU wait 在 CPU 计时之外，allocation 只统计调用线程。最终安静区间两轮中位数：普通逐 range 声明注册约 **0.748–0.755 ms**、执行约 **9.290–9.375 ms**；共享集合注册约 **0.0069–0.0070 ms**、执行约 **0.5799–0.5813 ms**。调用线程 allocation 约 18.2 KiB；修复逐 range 状态提交前，同一压力模型曾达到约 301 MB/次。它是重复范围压力基准，不能作为正常游戏帧的倍速。

静态默认场景实际帧能够复用分析；此前完整 60 帧验证命中 53/60。在改用 tile/quad 前的一轮验证中，default 场景 transient 分配约 145 MiB，位移 hybrid 约 194 MiB；这些历史值只在当时场景和容量下成立，不是 UE 对照，也不是总显存。后续版本的实际分配见测量 JSON 中相应日志记录。

VSM 继续使用单份页深度缓存与选择性失效。独立静态/动态深度层会按默认 1024×128² 增加约 64 MiB 深度和 21.33 MiB Hi-Z，还需要合成和双份管理。当前先保证变化 ID 与旧/新 footprint 正确；未经“局部动态重复绘制静态 caster”实际耗时证明，不增加该双份存储。细页池满时按现有层级解析可用的更粗页；coarse 与 fine 的 allocation failure 分开统计，不能将池满表述为零失败。

最终稀疏运动抓帧：128 个实例仅 ID 0 移动，invalidation 常量读回 `ChangedInstanceCount=1`、24 个 shadow view；变化列表只有 `[0]`，1024 页池中 40 页需要绘制，coarse/fine allocation failure 为 0/5。64 行粗粒度版本在另一次抓帧为 64 个 changed caster、596 个活动页；两次动态姿态受运行时钟影响，故这些页数是场景观察而非固定姿态的收益百分比。逐行输入、40 页活动集和其余页复用已由最终 capture 验证。

尚未获得厂商 ISA/VGPR/spill、barrier stall、cache miss、实际 async 队列重叠墙钟或 UE 同场景帧时，因此本文不使用这些未测数据背书。优化实现、功能正确性测试和有限场景的测量结果各自成立，范围不互相替代。

**复现与前一轮验证**

普通构建和测试使用 `SomeEngine.slnx`。没有启用旧 harness 工作流、创建 numeric run 或增加 mandatory gate。

本段保留 Mesh、寄存器 Dice、硬件过滤和 tile/quad 后续修正前的测试与场景记录；后续结果及新出现的测试失败见文末，不将两轮结果混成一次全绿验证。

- [可复现场景工具](../../tools/SomeEngine.RenderScenarios/README.md)：Phong、不同细分因子、四种 mask 材质、WPO/alpha、纹理位移、单实例稀疏运动。工具在新的独立输出目录复制已构建 Runtime，并 cook 自己的 DXIL/SPIR-V 场景资源。
- [稀疏范围 CPU benchmark](../../benchmarks/SomeEngine.RenderGraph.Benchmarks/README.md)：保留同一普通 API 的逐范围与共享集合两种输入形式，输出可机器读取的 JSON。
- `SomeEngine.Render.Cluster.Tests`：139；`SomeEngine.Render.Tests`：81；`SomeEngine.RenderGraph.Tests`：68；`SomeEngine.Assets.Tests`：153；`SomeEngine.Runtime.Tests`：13；新增 GPU 压缩/分配/Hi-Z/区域/误差界集合：16。
- ECS Systems 最终 311/311 完整通过；此前一次运行的旧 allocation 测试出现短暂分配失败（buffer=3488 B），单独重跑与全套重跑均通过。没有修改该阈值来使结果变绿。
- 实际 1280×720 GPU-validation 场景覆盖静态与动态、hybrid 与全 HW、位移与 alpha、方向光/点光 VSM。`tessDropped`、`tessLimited`、fine/coarse 页分配失败分别记录；并未将画面有输出等同于所有质量目标都达成。

最终九组场景每组 24 帧，共 216 帧全部通过 GPU validation。覆盖默认场景静态/动态、稀疏运动、WPO/alpha hybrid/HW 动态、位移 hybrid/HW 静态及 hybrid 动态、四材质 HW 静态。删除失效 raster API 后，又在最终构建上复验四材质 HW 和默认动态场景，各 16 帧通过。全部场景 dropped 为 0；位移静态仍有 192 quality-limited，最终动态运行有 208，详见 [测量与验证数据](Render-Enhancement-Measurements-2026-09-05.json)。

原始 `.rdc`、逐事件 JSON、GPU 原始缓冲与日志保存在审查来源记录同一工作树外研究目录。仓库仅保留上述紧凑测量数据，避免将 capture 和重复 runtime 二进制纳入源码。

**后续修正：统一 view 表示、Mesh、寄存器 Dice 与 tile/quad**

单 view 不采用省字段的格式：`DrawRequest` 在单/多 view 都是 12 B，三字依次为 page offset、instance ID、12-bit cluster +20-bit view。单 view 时 view 值为 0；读写 stride、绑定与解码函数不变，没有增加单 view shader 变体。共享遍历节点在所有情况下都是 16 B 的 group/mask 表示；单 view 仍支付这份固定表示成本。这里不宣称“单 view 不记录 view 字段也能少写内存”。

普通 HW 光栅新增 Mesh 路径：每 32 个三角形的 batch 收集完整 uint8 局部索引范围的顶点 mask，只求值并导出实际引用的唯一顶点，最多 96 个。GPU 回归覆盖 0..255 索引、部分 batch、重复索引以及小 subgroup 的跨 wave 合并，避免把 importer 默认 64 顶点误当成文件格式限制。普通 VS/PS 也使用 wave 内相同顶点结果复用。HW tessellation 的 Mesh 路径每 patch 输出最多 45 个网格顶点、64 个三角形；VS/PS 保留按 factor 的 indexed draw 和顶点复用。indirect 参数由同一 bin 计数生成，命令录制按实际设备能力选择。Vulkan Mesh PS 的 per-primitive 输入单独标注，材质计算主体共用。

硬件选择在本仓库的 D3D12/Vulkan 后端实际为 Mesh → VS/PS。UE 的 Primitive 需要其平台编译入口，当前后端没有这个入口；没有加入一个永远不可执行的 Primitive 档位，也没有使用 Geometry Shader 代替。`--no-mesh-shaders` 可复现 VS/PS，`--hardware-raster` 可复现全 HW。选择 Mesh 并不等于已证明它在每个场景比 indexed VS 更快，实测结果另列。

SW Dice 按 factor 使用 1/4/16/32 lanes 合作处理 patch，每 lane 至多三个 float4 保存网格 clip position，通过 wave lane read 共享。旧 Dice LDS 缓存和同步已经删除；无效 patch 和最后一轮的无效三角形仍参与必要的 wave 操作，防止分歧读到未参与的 lane。当前入口明确要求 wave32（SM 6.6）；wave64-only 设备的寄存器 Dice 入口尚不支持，不能宣称完整硬件覆盖。Vulkan 后端现在把反射的 `ComputeWaveSize` 传入 subgroup size control，并检查特性/范围；真实 compute GPU 回读已验证要求的 32 lanes 生效。WARP 不支持该固定 wave32 PSO，Dice 回归使用硬件设备；普通无 64-bit 原子路径使用全 HW。

材质 shading 已贯穿 full tile/quad 的 count → reserve → scatter → consume。完整同材质 8×8 tile 一条 8 B；其它区域按 2×2 quad 的同材质 mask 生成条目，消费侧每四条 lane 对应空间 quad，屏蔽无效像素。各 bin 分别记录 tile/quad 数量和偏移，tile 调度一个 group，quad 每 group 处理 16 条；没有保留旧逐像素消费路径。`shadeBinTiles` 和 `shadeItems` 分别表示首 bin 的 full tile 数与全部工作条目数，不能再作为像素数解释。

位移高度改用硬件 `SampleLevel`，统一 linear-repeat、LOD 0；解析导数仍描述连续 bilinear 插值，硬件权重的量化不适合用 infinitesimal 数值差分验证导数。误差界额外覆盖硬件插值精度：HLSL 按至少 8 fractional bits，通用 SPIR-V 按 Vulkan 基础要求的至少 4 bits 保守处理。后一选择可能比现代设备实际精度更保守，不能把 patch 增加隐藏成零成本。[Direct3D 插值精度规则](https://microsoft.github.io/DirectX-Specs/d3d/archive/D3D11_3_FunctionalSpec.htm)、[Vulkan limits](https://docs.vulkan.org/spec/latest/chapters/limits.html)。正负 scale、UV seam、区间收敛及硬件高度/求值位置一致性通过 GPU 验证。

VSM 方面，本轮没有根据“UE 移除了动静分离”的假设重构缓存；已直接核查固定 UE5-main 提交并否定该前提。静态/动态缓存分离、Nanite LOD delta 的延迟失效预算、前帧性能驱动的 throttle 在同一源码中并存。具体链接见 [补充源码核查](Render-Enhancement-UE5-Source-Audit-2026-09-05.md)。本仓库继续使用单份深度缓存和变化 caster 的即时选择性失效，不能将其描述为已经实现 UE 的全部缓存策略。

本轮 `SomeEngine.slnx` 最终构建 0 warning、0 error；受影响的 20 个 shader asset 重新 cook DXIL 与 SPIR-V。Render.Cluster 139、Render 81、RenderGraph 68、Assets 153、Runtime 13，共 454 项通过；D3D12 GPU 检查 20 项通过（包括后补的 2 项 uint8 Mesh compaction），Vulkan compute 的普通/固定 wave32 两项通过。Vulkan 整帧验证仍未完成：环境缺少 validation layer，关闭验证后默认设备也不具备本次窗口需要的 presentation；compute 通过不能代替 Mesh、窗口或整帧验证。

额外 ECS Systems 311 项全套首次为 309/311；复跑为 310/311，保留 `WarmedBufferAdmission_DoesNotAllocatePerCallback` 的 `buffer=3648 B` 失败记录，另一项 `WorldClosing_RejectsUnrelatedJobCallback` 在复跑时通过。两项随后单独执行均通过。没有改这两个测试、放宽零分配断言或将整套结果写成通过；这两项不是本轮 Mesh/shading 代码覆盖的测试。

**后续最终抓帧与数据**

最新构建的位移三路径、WPO/alpha Mesh 与 hybrid 共五组、120 帧通过 GPU validation，stderr 全空，全部 `tessDropped=0`。此前本轮还执行了多材质三路径、WPO/alpha 三路径、两组动态场景及位移三路径，共 248 帧；这一轮合计 16 组、368 帧。逐组结果与最终格式修正前后的区分在 [测量 JSON](Render-Enhancement-Measurements-2026-09-05.json) 的 `followup.validation` 中。

同一 fresh 位移场景分别用 Mesh 全 HW、VS/PS 全 HW、hybrid 捕获第 20 帧；RTX 3080、D3D12、每个 capture 七次 RenderDoc 事件计数器重放。以下为对应事件总时间的样本中位数，单位 ms，不是有 async overlap 的整帧墙钟：

| 路径 | 光栅事件（不含 Dice） | SW Dice | Shade binning | Material shading |
|---|---:|---:|---:|---:|
| Mesh 全 HW | 0.189920 | — | 0.062336 | 0.093184 |
| VS/PS 全 HW | 0.164224 | — | 0.062656 | 0.091136 |
| Hybrid | 0.039616 | 0.042560 | 0.066816 | 0.102400 |

该场景 Mesh 的光栅事件约比 VS/PS 慢 15.6%；单独 HW tessellation 分别为 0.130912/0.112480 ms。由此不能宣称 Mesh 必然更快。hybrid 的 Dice 数值也不等于已完成与旧 LDS 算法的同条件 A/B，因此没有宣称寄存器比 LDS 快多少。

Mesh 与 VS/PS 的 scene depth、motion 原始数据逐字节一致。visibility 的队列 ordinal 因并行分配顺序不同而不一致；读取各自可见记录并以 page 内容 SHA-256、instance、cluster/view、triangle 解码后，差异像素为 **0**。page heap offset 也会随资产流送分配顺序变化，不能将地址本身当作资产身份。Mesh 与 hybrid 解码后的 visibility 有 **8** 个像素差异；scene depth 的最大绝对差为 0.00665045、全图平均为 1.5573e-8，motion 的最大差为 0.00046277。没有把 SW 与固定功能 raster 声称为逐位相同。

最终 color 仍不一致。Mesh/VS 的 RGBA 半精度分量最大绝对差为 0.0988846、全图平均为 6.7151e-6；两次 1024 页 VSM 池均已满，624 个 table entry 的 resident 状态不同，共同 resident 页的 world tag/generation 一致。Mesh/hybrid 的 color 最大差为 0.836243、平均为 2.0494e-5，816 个 table entry 的 resident 状态不同。以上明确记录完整画面的差异，没有用可见性一致代替最终 color 一致，也没有以有限抓帧证明所有差异的唯一原因。

这组主视图有 28,206 个有效像素，得到 352 个 full tile 和 1,611 个 quad，共 1,963 条工作记录。实际条目数据为 **15,704 B**；原 4 B/pixel 表示同一覆盖需要 **112,824 B**，减少 **86.08%**。这是实际工作记录的字节数，未包含 counter/offset，也不等于最坏容量或显存峰值下降。静态位移仍为 16,740 patches、207 quality-limited、0 dropped；本轮保留这些质量和容量事实。
