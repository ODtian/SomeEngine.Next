**Render enhancement 工作树对 UE5 源码的性能审查 — 2026-09-05**

**实施状态更新**：下文保留审查时的发现和 UE 源码依据，其中“当前”“尚未修改”“未做 GPU 验证”均指最初审查时点。后续优化、附带发现的正确性修复、逐项处置、实测数据与验证边界见 [优化实施记录](Render-Enhancement-Optimization-2026-09-05.md)。不要将历史问题清单当作优化后代码的现状，也不要将局部 kernel/CPU 基准收益当作整个渲染器相对 UE 的帧时结论。

**UE5-main 补充核查**：用户进一步提出硬件路径、寄存器 Dice、硬件过滤、quad/tile shading 与 VSM 分帧缓存问题后，另行读取 `ue5-main` 的 `feb0feb7f29bf3a990531f62f216223b9f0d1ea5`（2026-09-05 04:46:14 UTC，CL 57829839）。下列结论来自该固定提交的源码；它是本次读取的 main 状态，不代替前文 release 审查，也不声称 main 永远保持不变。

- 硬件选择确实优先 Mesh，再检查 Primitive。Primitive 使用平台能力与 `CFLAG_VertexToPrimitiveShader` 编译标志，并非通用 Geometry Shader。[NaniteCullRaster.cpp，硬件选择](https://github.com/EpicGames/UnrealEngine/blob/feb0feb7f29bf3a990531f62f216223b9f0d1ea5/Engine/Source/Runtime/Renderer/Private/Nanite/NaniteCullRaster.cpp#L4165)、[Primitive 编译](https://github.com/EpicGames/UnrealEngine/blob/feb0feb7f29bf3a990531f62f216223b9f0d1ea5/Engine/Source/Runtime/Renderer/Private/Nanite/NaniteCullRaster.cpp#L1889)。本仓库实现 Mesh 与高效 VS/PS；D3D12/Vulkan 后端没有该专用 Primitive 编译入口，不能将中间一档写成已经完成。
- Quad、full tile 和 loose quad 分别编码、计数及分配，确实是该源码中的有效实现。[NaniteShadeBinning.usf](https://github.com/EpicGames/UnrealEngine/blob/feb0feb7f29bf3a990531f62f216223b9f0d1ea5/Engine/Shaders/Private/Nanite/NaniteShadeBinning.usf#L204)。本仓库后续已实现完整 tile 与带 mask quad 的 count/reserve/scatter/consume 链。
- 该版本 `NaniteDice.ush` 仍将 `NANITE_TESSELLATION_DICE_USE_LDS` 设为 1，并在顶点缓存访问中说明宽 LDS load 的动机。这是 UE 的选择，不能证明本仓库相同选择更快。本仓库按用户要求使用寄存器缓存，没有用 UE 的宏值代替本地性能证据。[NaniteDice.ush](https://github.com/EpicGames/UnrealEngine/blob/feb0feb7f29bf3a990531f62f216223b9f0d1ea5/Engine/Shaders/Private/Nanite/NaniteDice.ush#L10)。
- **没有移除静态/动态缓存分离**：`CacheManager->IsCacheEnabled()` 时仍设置 `StaticCachedArrayIndex = 1`，另有动态 HZB 分离控制。[VirtualShadowMapArray.cpp](https://github.com/EpicGames/UnrealEngine/blob/feb0feb7f29bf3a990531f62f216223b9f0d1ea5/Engine/Source/Runtime/Renderer/Private/VirtualShadowMaps/VirtualShadowMapArray.cpp#L1042)。移除可配置开关或增加分帧处理，不等于移除了这份缓存。
- `r.Shadow.Virtual.DeferredInvalidationBudget` 将 deferred invalidation 的消费分摊到多帧；源码明确其当时唯一来源是 Nanite streaming LOD delta，默认 -1 不限量。这不支持把移动 caster 的失效统一延迟。[预算定义](https://github.com/EpicGames/UnrealEngine/blob/feb0feb7f29bf3a990531f62f216223b9f0d1ea5/Engine/Source/Runtime/Renderer/Private/VirtualShadowMaps/VirtualShadowMapArray.cpp#L119)。另有读取前帧性能数据、EMA 和 LOD bias 的 throttle 链，与静态缓存同时存在。[跨帧负载调节](https://github.com/EpicGames/UnrealEngine/blob/feb0feb7f29bf3a990531f62f216223b9f0d1ea5/Engine/Source/Runtime/Renderer/Private/VirtualShadowMaps/VirtualShadowMapArray.cpp#L2850)。本仓库维持单份深度缓存与选择性失效，是自身当前实现选择，不能归因为“UE 已证明分离不再必要”。

本次 main 文件和逐文件 SHA-256 保存在 [main 来源记录](F:/.codex/research/render-enhancement-20260905/ue5-main-20260905/sources.json)。后续正文仍是最初 release 审查记录。

结论：当前实现已经完成不少重要优化，但还不能认为“除两项已接受差异外，主要成本已经接近 UE5”。最明显的问题集中在：VSM 过早按物理页拆 view、进入 mesh hierarchy 后没有继续共享 view mask、SW 光栅逐三角形通用裁剪及整行扫描、tessellation 固定分层与低利用率 dicing、全 HW tessellation 重复 domain 求值、VSM Hi-Z 的全分辨率金字塔，以及真实渲染帧无法使用现有 RenderGraph 执行复用路径。

另外，当前未提交版本有两处 tessellation 上传/声明不一致，属于功能阻断，必须和性能问题分开看。下面的性能判断建立在源码执行结构上，不代表该路径已经完成实际 GPU 运行验证。

审查对象是 `F:\SomeEngine.Next-render-enhancement` 的 `codex/render-enhancement` 分支，HEAD 为 `a787000034e7eba7559d20299a2c704216444011`，连同审查时的全部未提交内容；审查开始和记录证据时均有 342 条 Git 状态记录。不能把报告理解为只评价这个 commit。

UE 对照直接通过已认证的 GitHub 访问 `EpicGames/UnrealEngine` 私有仓库，固定到 release 分支的 `16d75d84714512edfb744e1fd0a59e9c74d57873`，提交说明为 5.8.2 release，提交日期 2026-08-25。读取了 34 个相关 C++/shader 源文件，没有使用 UE 文档、博客或二手实现。远程链接均固定 commit。本地 229 个相关源文件及所取 UE 文件的 SHA-256 记录在 [审查来源记录](F:/.codex/research/render-enhancement-20260905/audit-manifest.json)。

证据分三种：**确定**表示源码直接给出工作量、分支或容量；**条件确定**表示指定功能/输入被启用后发生；**待测**表示收益、实际耗时或硬件行为仍需计数器。优先级表示修正的重要性，不是未经测量的 GPU 时间排序。

| 优先级 | 发现 | 证据强度 | 主要影响 |
|---|---|---|---|
| P0 | tessellation seed 上传名称不匹配；全 HW 还上传未声明的 dicing 参数 | 确定的调用链问题，未做实际 GPU 复现 | 无法据当前测试宣称 tessellation 已端到端可用 |
| P1 | 每个待重绘 VSM 页成为独立裁剪 view，树遍历按单 view 展开 | 确定 | 重复 root/instance/node 工作、队列流量和容量压力 |
| P1 | SW 每个三角形执行七平面裁剪，scanline 扫整行 bbox | 确定 | 三角形 setup、无效像素测试、潜在寄存器压力 |
| P1 | HW tessellation 固定 192 个 VS 输入，domain 按三角形角重复计算 | 确定 | 低因子 padding、高因子重复顶点计算 |
| P1 | SW tessellation 每 patch 逐 row 串行 dice | 确定 | 少量 LDS 节省换来多次同步和低 lane 利用率 |
| P1 | VSM Hi-Z 从原页尺寸开始存，每 mip 单独 dispatch | 确定 | 相同单份页金字塔约 4 倍容量，更多调度/中间写回 |
| P1 | 真实帧绑定/动态 pass 排除 RenderGraph 执行复用；上传细碎 | 确定结构，CPU 毫秒待测 | 每帧图解析、访问声明、映射/flush、分配 |
| P2 | 节点遍历默认固定 32 层；tess split 每材质 bin 固定 6 层 | 确定 | 空 indirect 调度和参数/依赖固定成本 |
| P2 | wave matching 逐不同 key 循环 | 确定最坏工作量，典型收益待测 | 不相干输入时，去重本身可能比被省掉的工作更贵 |
| P2 | SW/dice 没有接入 async raster；reset 和复用 scratch 扩大串行链 | 确定调度路径，重叠收益待测 | HW/SW 不能按 UE overlap 模式并行 |
| P2 | VSM invalidation 全实例扫描、单线程扫页矩形、缺少变形变化输入 | 条件确定 | 动态场景失效成本；可能漏失效或全局重绘 |
| P2 | 全分辨率 SoftwareDepth 仅作 neutral output 等可压缩资源 | 确定声明，实际峰值需图分配数据 | 无意义容量/clear，以及过大的统一队列上限 |
| 潜在正确性 | 未启用的 persistent traversal 缺少跨工作组 payload ready 协议 | 条件确定 | 开启能力后可能读到尚未写好的节点 |

**1. 多 view：批量提交已有，但共享层级遍历只做到 seed 的局部粗层。**

当前 seed 用一个 64 线程组处理 64 个 instance，并针对最多 32 个 view 计算粗可见 mask。这不是完全没有共享：一个组内 instance transform/root bounds 会用于同组的多个 view。问题是随后把每个存活位展开为单独的 `(root, instance, viewIndex)` 节点；后续 traversal node 和 leaf candidate 都只有单个 view。共同的 mesh node 仍然按 view 重读、重投影、重测 LOD/Hi-Z。

更早的粗层也有限：shader 先把 64 个 instance 的 root 与 transform 都读出并转换包围盒，再在 LDS 中构造 union；一个组处理完 32 个 view 后，下一组又要重复这些读取和归约。这个粗组按 instance 编号划分，没有看到基于空间聚类的 chunk hierarchy。因此不能期待其 union 总是紧致，也不能把它等同 UE 的空间 scene hierarchy。

VSM 将这件事放大了。`CompactVirtualShadowViews` 实际遍历 `ActivePhysicalPages`，为每页构造 page crop 矩阵、128×128 screen size、独立 Hi-Z layer 和 raster target。假设一个 source shadow view 有 1024 个 dirty page，仅 seed 的 view 维度就产生 32 个 view 批次；通过粗剔除后还会在根到叶路径按页产生重复节点。**1024 并不意味着每个节点必然访问 1024 次**，因为 page frustum 会裁掉分支；确定的问题是高层工作和重叠节点的数量依赖 page 数量，而不是只依赖 source view 数量。

UE 的 VSM view compaction 是 primary view × 有效 mip；在 cluster 可见性确定后，才根据 page flags 和有限 page window 输出 raster 工作。UE 的 page overlap 查询还能使用分层 page flags/receiver mask，避免只有“大矩形非空”这一种判断。

依据：[本地 seed 粗层与 mask](F:/SomeEngine.Next-render-enhancement/assets/Shaders/cluster_bvh_traverse_seed.slang:106)、[seed 按 view 展开](F:/SomeEngine.Next-render-enhancement/assets/Shaders/cluster_bvh_traverse_seed.slang:284)、[本地每页构造 view](F:/SomeEngine.Next-render-enhancement/assets/Shaders/vsm_view_compact.slang:108)；[UE 按 primary view/mip compact](https://github.com/EpicGames/UnrealEngine/blob/16d75d84714512edfb744e1fd0a59e9c74d57873/Engine/Shaders/Private/VirtualShadowMaps/VirtualShadowMapCompactViews.usf#L53-L113)、[UE cluster 后输出 page window](https://github.com/EpicGames/UnrealEngine/blob/16d75d84714512edfb744e1fd0a59e9c74d57873/Engine/Shaders/Private/Nanite/NaniteClusterCulling.usf#L877-L934)、[UE 分层页重叠测试](https://github.com/EpicGames/UnrealEngine/blob/16d75d84714512edfb744e1fd0a59e9c74d57873/Engine/Shaders/Private/VirtualShadowMaps/VirtualShadowMapPageOverlap.ush#L88-L114)。

对用户要求的判断：**“多 view 尽量共用一次层级遍历”尚未达到。** 一个 submit、一个 indirect dispatch API 或一组 pass，不等于 node 访问已共享。

符合约束的最终方向是让通用管线接受 view group、活动 view mask、目标区域查询和输出映射能力；instance/mesh node 以 group+mask 前进，共用节点加载与物体空间数据，每个 view 保留自己的裁剪、LOD 和遮挡结果。在工作明显分歧、到叶/光栅阶段或目标区域输出阶段再展开；VSM 在外部提供稀疏页区域信息与页映射，管线内部不出现 shadow/page-cache 语义。不能简单对整个 view group 一律采用最高 LOD，否则会用几何过度细分抵消遍历节省。

这里需要准确区分 UE 现状与用户的额外要求：UE 的 scene chunk 层持有 view mask，但其 mesh node candidate 也有单个 ViewId。因此“所有 view 一直共享到 mesh hierarchy 底部”不是可以原样复制的 UE 默认行为。UE 可直接借鉴的关键是**把物理页展开推迟到 cluster 之后**以及空间粗层组织；用户要求的更深层 mask 共享需要在此基础上实现。[UE scene chunk view mask](https://github.com/EpicGames/UnrealEngine/blob/16d75d84714512edfb744e1fd0a59e9c74d57873/Engine/Shaders/Private/Nanite/NaniteInstanceHierarchyCulling.usf#L106-L163)、[UE mesh candidate 的单 ViewId](https://github.com/EpicGames/UnrealEngine/blob/16d75d84714512edfb744e1fd0a59e9c74d57873/Engine/Shaders/Private/Nanite/NaniteClusterCulling.usf#L374-L380)。

**2. 节点遍历：固定最大 32 层有额外成本，不能用“UE 默认 persistent kernel”解释差距。**

本地默认 `MaxTraversalDepth=32`，普通路径注册并依赖串联 32 次 node indirect dispatch。没有 continuation 时有主/后两组遍历；有效 continuation 场景下还注册另外两组，即最多 128 个 node-level pass。实际 GPU 非零工作取决于层数、计数和 pass 启用状态，不能把 128 当作每帧都有 128 次完整计算。

即使后半层 indirect group count 为零，CPU 图节点、参数和依赖仍在，GPU 命令处理也不免费。真实层深来自内容，应由 BVH 构建/流送提供安全的最大层数；保留深度上限作为验证约束，与调度次数分开。

UE 5.8.2 的 persistent culling 开关默认就是 0，普通路径按 streaming manager 的实际最大 hierarchy level 循环。**逐层 dispatch 和最终 cluster candidate 中间队列本身不是偏离 UE 的错误。** 不应为了消灭 kernel 边界就默认开启 persistent，尤其本地 D3D12 的 compute forward-progress 能力明确为 false。

依据：[本地默认值](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.Render.Cluster.Pipeline/ClusterPipelineOptions.cs:21)、[固定 32 层循环](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.Render.Cluster.Pipeline/ClusterPipelineSystem.Passes.cs:827)；[UE persistent 默认关闭](https://github.com/EpicGames/UnrealEngine/blob/16d75d84714512edfb744e1fd0a59e9c74d57873/Engine/Source/Runtime/Renderer/Private/Nanite/NaniteCullRaster.cpp#L322-L327)、[UE 按场景实际层深](https://github.com/EpicGames/UnrealEngine/blob/16d75d84714512edfb744e1fd0a59e9c74d57873/Engine/Source/Runtime/Renderer/Private/Nanite/NaniteCullRaster.cpp#L4599-L4614)。

未启用分支还有一项正确性风险：persistent producer 先增加可见 tail，再写 `TraversalNodes`；consumer 只依据 head/tail 决定读取，没有逐条 ready 标记或等价设备范围顺序。group barrier 不能使其他组等到 payload 写完。UE persistent 代码有 ready sentinel/coherent load/device barrier。当前后端没有开启这条路径，所以它不是当前热点，但也不能作为立即可用的 fusion 优化。[本地先增加队列计数再写节点](F:/SomeEngine.Next-render-enhancement/assets/Shaders/cluster_bvh_traverse_core.slang:481)、[UE 检查节点数据 ready](https://github.com/EpicGames/UnrealEngine/blob/16d75d84714512edfb744e1fd0a59e9c74d57873/Engine/Shaders/Private/Nanite/NaniteClusterCulling.usf#L645-L660)、[UE 设备范围 barrier](https://github.com/EpicGames/UnrealEngine/blob/16d75d84714512edfb744e1fd0a59e9c74d57873/Engine/Shaders/Private/Nanite/NaniteHierarchyTraversal.ush#L133-L180)。

**3. SW 光栅：七平面 clip 与 bbox scanline 是当前最具体的指令浪费。**

本地普通 SW 和 tess dice 最终调用同一 triangle raster helper。每个 active 三角形都进入七平面多边形裁剪，使用两个最多 16 个 float4 的临时数组，逐平面生成/搬运顶点，再 fan triangulate。没有“所有顶点已在裁剪范围内”的直接 setup 快路径。多数小三角形都不需要这种工作。

UE 在 cluster culling 中将需要 clipping 的普通 cluster 路由 HW，小三角形 SW 路径直接 setup/raster。因此对普通 hybrid 光栅，应把 near/clip 边界条件尽可能在前面分类；tessellated patch 需要基于变形后的保守界处理，不能直接照搬未变形 cluster 的判断。仍需要软件裁剪的子集也应该有 trivial accept/reject，再走通用 clip。

本地所谓 scanline 只把一个任务设为一行，仍从 bbox 的 firstX 扫到 lastX；每个 x 都计算三条边然后 reject。UE 根据边交点先算该行 x0/x1，只扫描有效区间。细长、斜向三角形的包围盒面积可远大于覆盖面积，当前无效 edge test 很容易放大。

clip 数组和大 task payload 确实带来更高的源码临时量；**是否 spill、VGPR 数、occupancy 损失尚未通过 SPIR-V/DXIL/ISA 或 GPU profiler 确认**，报告不将其写成实测事实。

依据：[每个三角形调用通用 clip](F:/SomeEngine.Next-render-enhancement/assets/Shaders/cluster_raster_shared.slang:258)、[整行 bbox scanline](F:/SomeEngine.Next-render-enhancement/assets/Shaders/cluster_raster_shared.slang:223)；[UE clipping 路由 HW](https://github.com/EpicGames/UnrealEngine/blob/16d75d84714512edfb744e1fd0a59e9c74d57873/Engine/Shaders/Private/Nanite/NaniteClusterCulling.usf#L858-L860)、[UE 有效 scanline 区间](https://github.com/EpicGames/UnrealEngine/blob/16d75d84714512edfb744e1fd0a59e9c74d57873/Engine/Shaders/Private/Nanite/NaniteRasterizer.ush#L230-L288)。

**4. Tessellation：中间几何已经消除，但 split 的固定开销仍随材质 bin 线性增长。**

当前 patch 是 24 bytes：原 visible cluster/triangle、bin、压缩 barycentric corner、因子/深度。保留 compact patch 工作队列，没有完整微三角形数组，没有全局变形顶点数组，domain evaluation 直接融入 dice/raster。最终因子由 splitter 保存，dice 不重复执行误差分析。这些是实际已落地的优化。

目前调度是每个 tessellation 材质 invocation：scratch reset → seed → 6 次 split；全部 bin 处理完才做一次 final-patch count/reserve/scatter，再按 bin dice。**不是每层都重复 binning**。额外成本是每个 bin 都承担完整固定 split 链以及相应参数和依赖；无 patch 的深层也保留 indirect 调度。

UE 的 `AddPass_PatchSplit` 对全局 patch queue 分层 split，之后组织最终 raster bins。其 split shader 也会重新取源三角形，因此“每层重载三角形”并非本地独有。真正需要优化的是本地 per-bin 调度以及 domain/误差工作在不同 entry 中能否共用相同的计算结果。

如果不同 domain 类型确实需要不同 shader，按 shader/domain 特化分组是合理边界；同一特化下不同材质参数不应强制再产生 6 层独立 launch。所需保守几何/误差数据也可用紧凑属性提供给 splitter，不能为了 global split 去掉用户自定义曲面的误差保证。

UE split record 为 16 bytes，visible patch 根据 permutation 存 8-byte 引用+pattern 或 16-byte inline 数据。本地 24 bytes 有进一步压缩空间，但字段范围/功能不同，不能仅以结构体大小判定应直接改为 8 bytes。

依据：[本地 split 与唯一一次 final binning](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.Render.Cluster.Pipeline/ClusterPipelineSystem.Passes.cs:1536)、[24-byte patch 相关类型](F:/SomeEngine.Next-render-enhancement/assets/Shaders/cluster_tessellation_types.slang:1)；[UE 全局 patch split 调度](https://github.com/EpicGames/UnrealEngine/blob/16d75d84714512edfb744e1fd0a59e9c74d57873/Engine/Source/Runtime/Renderer/Private/Nanite/NaniteCullRaster.cpp#L6332-L6478)、[UE 8/16-byte final patch 表示](https://github.com/EpicGames/UnrealEngine/blob/16d75d84714512edfb744e1fd0a59e9c74d57873/Engine/Shaders/Private/Nanite/NaniteSplit.usf#L265-L274)。

**5. SW dicing：为了省 432 bytes LDS，支付了明显的串行化成本。**

一个 final patch 一个 32 线程组，factor 最大 8，使用 18 个 float4 的 rolling row LDS，即 288 bytes。factor=8 时完整三角网格只有 45 个唯一顶点，完整 clip cache 也只有 720 bytes；rolling row 节省 432 bytes。

代价是初始一次 group barrier，加每 row 两次，共 17 次；8 个 row 分别处理 15、13、11、9、7、5、3、1 个三角形，总共 64 个。三角形输入端的有效 lane 数平均为 8/32；8 个 row 对应 256 个 lane 位置承载 64 个源三角形，若将 64 个三角形平坦排布只需要两个 32-lane 批次。**这里的 4 倍是发出 triangle setup 的 lane 位置比，不是整段 raster 4 倍耗时**；下游 wave work queue 会重新分配像素任务。

UE dice 使用适配三角形索引的 wave 工作组织、寄存器缓存及 lane shuffle；patch setup 也可在一个 wave 中加载多个 patch。优先比较完整小型 LDS cache、寄存器 wave cache、多个 patch 合批，而不是默认“LDS 越少越快”。选择应由实测寄存器数、有效 wave 占用和 barrier stalls 决定。

依据：[本地逐 row dice 与同步](F:/SomeEngine.Next-render-enhancement/assets/Shaders/cluster_tessellation_core.slang:702)；[UE dice 顶点复用与 shuffle](https://github.com/EpicGames/UnrealEngine/blob/16d75d84714512edfb744e1fd0a59e9c74d57873/Engine/Shaders/Private/Nanite/NaniteDice.ush#L141-L207)、[UE 多 patch setup](https://github.com/EpicGames/UnrealEngine/blob/16d75d84714512edfb744e1fd0a59e9c74d57873/Engine/Shaders/Private/Nanite/NaniteRasterizer.usf#L914-L995)。

**6. 全 HW fallback：保留 tessellation 的方向正确，当前 non-indexed 展开仍有显著可避免开销。**

当前每个 patch 的 draw 固定为 192 vertices，即 8²×3，无论最终 factor 是多少。VS 用 microTriangle 范围判断抛弃 padding，并对有效的每个微三角形角点取源三角形、计算 barycentric、调用 domain.position。

| 最终 factor | 有效微三角形 | 有效角点/当前 domain.position 调用数 | 唯一网格顶点 | 发给 VS 的顶点数 |
|---:|---:|---:|---:|---:|
| 1 | 1 | 3 | 3 | 192 |
| 2 | 4 | 12 | 6 | 192 |
| 4 | 16 | 48 | 15 | 192 |
| 8 | 64 | 192 | 45 | 192 |

factor=1 的 189 个 padding 输入有早退，不会都执行完整 domain；但 VS 输入和 patch 元数据访问仍有额外工作。factor=8 没有 padding，却把理论 45 个唯一顶点的 domain evaluation 做成 192 次，4.27 倍。这个比例是求值次数，不是帧时。

应保留 patch 的紧凑表示，在 HW 端按实际 pattern/factor 使用可复用的索引模式和唯一顶点 ID，或在支持 mesh shader 的设备上一次导出唯一顶点及 primitive。mesh shader 能力与 int64 atomic 能力是独立条件；不能把 no64 等同于不能做顶点复用。传统 indexed VS 的实际 post-transform reuse 还要测，不能承诺精确只执行 45 次。

还需注意：UE 5.8.2 的 HardwareOnly raster 调度明确不启用 Nanite tessellation。用户要求的“无 atomic64 仍完整保留 tessellation”超出该 UE 路径，不能把关掉特性当作性能对齐办法。普通三角形 HW fallback 的非索引 corner VS 在 UE 也存在；它不是本地独有错误，差距主要在没有选用 UE 的 mesh/primitive reuse 路径，以及本地新增 HW tessellation 的固定展开。

依据：[固定 192 顶点 draw](F:/SomeEngine.Next-render-enhancement/assets/Shaders/cluster_tessellation_bin.slang:190)、[每角点执行 domain](F:/SomeEngine.Next-render-enhancement/assets/Shaders/cluster_tessellation_entries.slang:82)；[UE HardwareOnly 排除 tessellation](https://github.com/EpicGames/UnrealEngine/blob/16d75d84714512edfb744e1fd0a59e9c74d57873/Engine/Source/Runtime/Renderer/Private/Nanite/NaniteCullRaster.cpp#L6074-L6075)、[UE mesh shader 唯一顶点导出](https://github.com/EpicGames/UnrealEngine/blob/16d75d84714512edfb744e1fd0a59e9c74d57873/Engine/Shaders/Private/Nanite/NaniteRasterizer.usf#L3123-L3152)、[UE 也保留普通 corner VS](https://github.com/EpicGames/UnrealEngine/blob/16d75d84714512edfb744e1fd0a59e9c74d57873/Engine/Shaders/Private/Nanite/NaniteRasterizer.usf#L3161-L3222)。

**7. Wave/LDS：大量正确使用已经存在，但通用 matching 与 seed 归约值得优先缩减。**

已有优化包括 traversal/candidate 的 wave prefix+一次 wave 级分配、bin 写入合并、普通 SW 顶点 used-mask 与局部 LDS cache、tessellation 局部顶点复用。不能笼统判断“wave 没用起来”。

问题在 `WaveMatchingLanes`/`WaveMatchingLanes2`：反复选 remaining mask 的首 lane，读取 key，再 ballot 去掉同 key lanes。一个 wave 内有 k 个不同 key 就执行 k 轮；64-lane wave 最坏 64 轮。每个三角形的三个顶点都调用匹配，split 输入若来自不同 cluster，去重收益很低，却支付完整搜索。shade 输入仅按材质 bin 排列，也不保证有足够 triangle/vertex 局部性。

已知同一 patch/cluster 的波内工作应直接用索引/shuffle；一般输入需要 all-equal/已知分组快路径，并比较不去重的成本。真正的 subgroup match 只能在 Slang 目标和设备能力支持时使用，不能把一个不存在的统一指令当成既定修正方案。

seed 的 min/max union 有初始同步、六轮 LDS reduction 同步和 mask 同步，共 8 次。64 实例完全可以比较 wave min/max → 每 wave 一个结果 → 小型跨 wave 合并的结构。但更大的收益在于避免对每组 32 个 view 重新读所有实例：持久维护空间 chunk 的保守 bounds，可以让 coarse rejection 发生在全实例取数之前。

依据：[逐 key matching](F:/SomeEngine.Next-render-enhancement/assets/Shaders/wave_aggregation.slang:30)、[vertex 去重与字段 shuffle](F:/SomeEngine.Next-render-enhancement/assets/Shaders/vertex_consumer_cache.slang:16)、[seed LDS reduction](F:/SomeEngine.Next-render-enhancement/assets/Shaders/cluster_bvh_traverse_seed.slang:153)、[普通 SW 局部 cache](F:/SomeEngine.Next-render-enhancement/assets/Shaders/sw_raster_core.slang:60)。

**8. 依赖与 kernel fusion：atomic merge 已有，HW/SW overlap 没真正接通。**

输出资源已经声明 atomic-mergeable hazard，RenderGraph 也有相应处理，不能说所有 raster 都被普通 UAV barrier 强制串行。然而 `AddComputePass` 的 async 参数默认 false，只有选中的 shade/bin 调用传 true；普通 SW raster 和 tess dice 都留在 graphics queue。只打开 `EnableAsyncCompute` 不会得到 UE 的 HW/SW raster overlap。

UE 在 overlap 调度下把 SW raster 放到 async compute，并对共享原子输出使用 skip-barrier UAV。当前可继续利用已有 atomic hazard 表达，只将真正独立的 SW/HW 分支安排到不同队列，然后在解析 depth/visibility 或后续 Hi-Z 消费之前汇合。是否更快取决于设备和 shared bandwidth，不能全设备强开。

小型 counter/reset 多次走 copy pass，加上各 epoch/bin 重用同一批 scratch，扩大了串行范围。可把独立计数清零、indirect args 初始化合并到同队列的 reset kernel；按数据需要而不是“上一阶段全部结束”添加依赖。scratch 分配需要权衡显存与 overlap，不建议无条件每个 view 复制全部缓冲。

适合保留的 kernel 边界包括全局 count→reserve→scatter、每层需要可见计数的 traversal，以及 Hi-Z 超出一个工作组覆盖范围的上层归约。没有跨组同步保证时，不能为了 fusion 去掉这些边界。真正值得 fuse 的是局部 vertex/domain→raster、page depth decode→首层 min reduction、小型 resets、同一 shader 特化下的 patch 工作；其中第一项本地已经完成。

依据：[async 默认 false 与队列选择](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.Render.Cluster.Pipeline/ClusterPipelineSystem.Passes.cs:2970)、[本地 atomic mergeable 绑定](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.Render.Cluster.Pipeline/ClusterPipelineSystem.Materials.cs:526)；[UE skip barrier 与 async raster](https://github.com/EpicGames/UnrealEngine/blob/16d75d84714512edfb744e1fd0a59e9c74d57873/Engine/Source/Runtime/Renderer/Private/Nanite/NaniteCullRaster.cpp#L6077-L6096)。

**9. VSM Hi-Z：单份容量约四倍，还多出逐 mip 全局中间结果。**

默认 PageSize=128、AtlasSize=4096，对应 1024 个物理页、64 MiB R32 atlas。本地持久页 Hi-Z 是 128×128×1024、8 mips、R32F；其 texel payload 为：

- 本地：1024×4×(128²+64²+…+1²) = 89,477,120 bytes，约 85.33 MiB。
- 相同页数、从 64×64 起的单份金字塔：1024×4×(64²+32²+…+1²) = 22,368,256 bytes，约 21.33 MiB。
- 差额正好 64 MiB；容量比约 4.0002。数值不含驱动对齐/metadata，也没有把 UE 可选的 static/dynamic 双 Hi-Z 混到单份比较里。

本地 init kernel 将 atlas 原深度一比一解码到 mip0；随后每个 mip 单独 dispatch，共每次 build 8 个 pass，并在中间/最终时点各构建一遍。UE 首次 gather 2×2 直接生成半分辨率 mip0，一个组内继续生成前五级，再单独构建顶部两级，即每次 build 的 reduction 部分 2 个 pass，另有 dirty-page selection。

可直接借鉴的方案是删掉冗余全尺寸 mip0、融合深度解码与 2×2 min、工作组内归约多个 mip，再保留必要的 top-level kernel。要一起调整投影到 Hi-Z 的尺度和保守采样规则，不能只缩小纹理而保持旧采样参数。减少一级基础分辨率可能降低最精细遮挡精度，但符合 UE 的取舍，不应制造 false occlusion。

已有 active/dirty-page 间接处理和持久 cache 是正确基础，不应误报为“每次全 atlas 重建”。真正多余的是选中页的全尺寸副本和逐层拆分。

依据：[本地持久页 Hi-Z 容量](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.Render.Features/Shadows/VirtualShadowFeature.cs:145)、[本地每 mip 注册 pass](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.Render.Features/Shadows/VirtualShadowFeature.cs:298)、[原尺寸 depth copy](F:/SomeEngine.Next-render-enhancement/assets/Shaders/vsm_page_hiz.slang:18)；[UE 半分辨率 pool](https://github.com/EpicGames/UnrealEngine/blob/16d75d84714512edfb744e1fd0a59e9c74d57873/Engine/Source/Runtime/Renderer/Private/VirtualShadowMaps/VirtualShadowMapArray.cpp#L1161-L1167)、[UE gather 直接生成半尺寸 mip0](https://github.com/EpicGames/UnrealEngine/blob/16d75d84714512edfb744e1fd0a59e9c74d57873/Engine/Shaders/Private/VirtualShadowMaps/VirtualShadowMapPhysicalPageManagement.usf#L1741-L1757)、[UE 两段 reduction 调度](https://github.com/EpicGames/UnrealEngine/blob/16d75d84714512edfb744e1fd0a59e9c74d57873/Engine/Source/Runtime/Renderer/Private/VirtualShadowMaps/VirtualShadowMapArray.cpp#L4865-L4913)。

**10. VSM cache/invalidation：已避免全量清请求，但变化跟踪还不够。**

本地 request 用 frame stamp，页 cache 持久化，有 age/eviction buckets、dirty page 输出和页局部 Hi-Z。这些都已实现。

剩余问题有四项。首先，caster invalidation 每帧检查所有 instance 的前后 transform；发生变化后，一个线程再循环所有 shadow view，并逐页循环两个包围矩形。接近 near plane 时保守投影可退化为整图矩形。其次，transform 不变的动画顶点、WPO/domain/material 参数变化没有看到同等粒度的 dirty 输入，可能漏失效；geometry/material mapping revision 等结构变化则走整体 cache generation 重置。第三，静态/动态 caster 共用重绘内容，动态物体覆盖页会带上静态部分重绘。第四，allocator 扫描所有 active table entries 判断 frame stamp，分配/eviction 对热点计数逐 lane CAS，缺少 UE 的 wave 聚合 push/pop。

UE 的实现提供变形状态/失效队列、static/dynamic 页状态、wave 级页列表操作和基于 dirty flags 的 HZB page selection。复制全部复杂性不是目标；本地需要变化集合驱动 invalidation、空间/光源预筛选、页矩形并行展开，以及对静态缓存是否值得分离作明确选择。单实例变化不应只能在“漏更新”和“重置全 cache”之间选择。

此处没有把本地 PCF 与 UE SMRT 的采样数直接当作性能差距：画质、遮挡搜索和滤波算法不同，不能在未设同等质量目标时直接比较。

依据：[全实例 transform 扫描](F:/SomeEngine.Next-render-enhancement/assets/Shaders/vsm_caster_invalidation.slang:174)、[单线程页矩形失效](F:/SomeEngine.Next-render-enhancement/assets/Shaders/vsm_caster_invalidation.slang:134)、[结构变化整体 cache 决策](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.Render/Shadows/VirtualShadowMap.cs:451)、[逐 lane CAS 分配](F:/SomeEngine.Next-render-enhancement/assets/Shaders/vsm_allocate_pages.slang:31)；[UE GPU cache invalidation](https://github.com/EpicGames/UnrealEngine/blob/16d75d84714512edfb744e1fd0a59e9c74d57873/Engine/Shaders/Private/VirtualShadowMaps/VirtualShadowMapCacheGPUInvalidation.usf#L66-L141)、[UE wave 页列表操作](https://github.com/EpicGames/UnrealEngine/blob/16d75d84714512edfb744e1fd0a59e9c74d57873/Engine/Shaders/Private/VirtualShadowMaps/VirtualShadowMapPhysicalPageManagement.usf#L71-L104)、[UE dirty page HZB 选择](https://github.com/EpicGames/UnrealEngine/blob/16d75d84714512edfb744e1fd0a59e9c74d57873/Engine/Shaders/Private/VirtualShadowMaps/VirtualShadowMapPhysicalPageManagement.usf#L1644-L1678)。

**11. CPU：保留拓扑不等于实际帧省掉图解析，当前热路径存在实质重复工作。**

`FrameExecutor` 只有没有动态 buffers/views/passes/access/orders、没有 overlays/external bindings 等条件时，才允许复用执行结果。真实 Cluster 帧每帧绑定 external resources、创建 views/overlays，而且 `ClusterFrameUploads.CompleteFrame` 每帧新建资源数组和 dynamic finalization pass，因此直接不满足条件。后续每帧仍走 BuildRows、值解析、liveness、hazard、schedule、lifetimes、materialization、synchronization。

这不是说 UE RDG 每帧不建图；UE 也有动态建图。能确认的差距是本地声称可保留的结构没有覆盖真实使用方式，无法把空图/简单 copy 的低分配数据外推到 Cluster frame。

上传也按每个小 uniform 单独管理。每次 Write 都做 label occurrence 字典查询、按 256-byte 大小清栈和复制、单独 Map/Copy/Flush/Dispose。tessellation 的 bin×6×epoch 等常量每帧照写，且每帧重新构建相关描述和 order 集合。应让已声明资源用直接 handle 更新、把 frame uniform 连续批量写入常驻映射内存、只在结构变化时重建对应访问/调度内容。无需引入新的多层管理架构。

流送还有另一类 CPU 放大：没有 active mesh 更新批次时，`CaptureResidentPageRanges` 会锁住 mesh 状态、枚举全部 resident allocations、建 list、排序、合并并转 array。随后每个 PageHeap reader pass × 每个合并后 resident range 添加一条读访问。这里是**合并后 range 数量**，不是一定每页一条，但碎片化与大量 pass 叠加会让 CPU hazard 输入膨胀。驻留范围应在驻留集合变化时更新，并由共享访问描述消费；仍需正确保留异步流送的范围依赖。

已有材质 stage 拓扑版本复用和 RenderGraph 并行命令记录应保留，问题不是完全没有 CPU 优化，而是尚未覆盖实际帧中的主要乘数项。

依据：[执行复用资格与完整解析](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.RenderGraph/FrameExecutor.cs:123)、[每帧动态 finalization](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.Render.Cluster.Pipeline/ClusterFrameUploads.cs:227)、[逐 buffer Map/Flush](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.Render.Cluster.Pipeline/ClusterFrameUploads.cs:393)、[每 pass × resident range 声明](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.Render.Cluster.Pipeline/ClusterPipelineSystem.Resources.cs:792)、[resident ranges 重建](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.Render.Cluster/ClusterMeshes.cs:893)。

**12. 显存与压缩：区分声明容量、同时存活容量和实际分配峰值。**

默认 MaxCandidates=1,048,576，本地大型中间资源声明如下：

| 资源 | 元素容量 × stride | 声明大小 |
|---|---:|---:|
| TraversalNodes | 1 Mi × 12 B | 12 MiB |
| CandidateClusters | 1 Mi × 12 B | 12 MiB |
| Phase2CandidateClusters | 1 Mi × 12 B | 12 MiB |
| VisibleClusters | 1 Mi × 12 B | 12 MiB |
| BinnedClusters | 9 Mi × 8 B | 72 MiB |
| 两个 tess work 队列 + final patches | 3 × 1 Mi × 24 B | 72 MiB |
| 上述合计 | — | 192 MiB |

这是命名资源容量之和，不是 GPU residency 峰值。本地 allocator 支持 aliasing，实际活跃功能、lifetime overlap 和 liveness 会改变真实分配。也不能把所有这些 scratch 再按 viewCount 直接相乘，因为多个工作阶段复用同一批 buffer。

优先减少按页展开产生的工作项、按真实内容的 binned entry 上界和 tess patch 范围设置容量、缩小 final patch 表示。不要通过强行把所有字段压成几位来损失 view 范围或 tessellation 功能。

还有一项更直接的浪费：hybrid 模式仍创建并 clear 全分辨率 R32 `SoftwareDepth`，但当前 SW 结果写入 64-bit winner；该旧资源主要作为 `NeutralPolicyOutput` 绑定，neutral target 的 extent 为零，主 view 根本不写它。no64 模式的 neutral output 已经是 1×1。可删除旧全尺寸 software depth 路径，以最小 neutral resource 满足真正需要的绑定，或通过 shader 特化消除该绑定。每 view 的 1080p 为 7.91 MiB、4K 为 31.64 MiB，另有 clear 成本。

顶点压缩不是缺失：已有 cluster-local 可变 bit width position/UV、packed normal/tangent，和 shader 位流解码。UE 用更完整的 cluster/index/attribute 压缩，但不能仅凭名字或结构体存在就断言本地仍传 float32 全量顶点。进一步比较应看实际每 triangle bytes、cache miss 和 decoder 指令成本。

依据：[所有大 buffer 容量声明](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.Render.Cluster.Pipeline/ClusterPipelineSystem.Resources.cs:164)、[全尺寸 software depth](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.Render.Cluster.Pipeline/ClusterPipelineSystem.Resources.cs:420)、[帧目标 clear 仍包含它](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.Render.Cluster.Pipeline/ClusterPipelineSystem.Resources.cs:1415)、[当前 winner 写入](F:/SomeEngine.Next-render-enhancement/assets/Shaders/cluster_visibility_raster_output.slang:7)、[实际顶点编码器](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.Assets.Importers/Importers/StandardClusterVertexEncoder.cs:63)。

**13. 功能阻断与验证结果。**

tess seed pass 声明上传资源用的是 `Cluster phase one tessellation uniforms`，逐帧写入用的是 `Cluster phase one tessellation seed uniforms`；phase two 同样。上传器按字符串精确查找，没有匹配时直接抛 InvalidOperationException。当前没有找到另一处声明 seed 标签。实际启用 tessellation invocation 并经过该逐帧上传路径就会触发。

即使修正 seed 标签，全 HW 模式仍无条件写 `dicing uniforms`，而 pass 构建明确不创建 dice；它创建的是 `tessellation hardware uniforms`，逐帧写入链中没有对应标签。这是独立的 mode 分支不一致。这里只指出需要统一已有声明/写入路径，不建议加兼容别名或 fallback。

依据：[seed 声明](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.Render.Cluster.Pipeline/ClusterPipelineSystem.Passes.cs:1558)、[seed 写入](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.Render.Cluster.Pipeline/ClusterPipelineSystem.Resources.cs:1091)、[无条件 dicing 写入](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.Render.Cluster.Pipeline/ClusterPipelineSystem.Resources.cs:1136)、[HW 参数声明](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.Render.Cluster.Pipeline/ClusterPipelineSystem.Passes.cs:2131)、[找不到标签直接抛异常](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.Render.Cluster.Pipeline/ClusterFrameUploads.cs:184)。

本次验证：

- `dotnet build SomeEngine.slnx --no-restore --verbosity minimal`：通过，0 warnings、0 errors。
- `dotnet test tests/SomeEngine.Render.Cluster.Tests/SomeEngine.Render.Cluster.Tests.csproj --no-restore --verbosity minimal`：135/135 通过，0 skipped。
- 上述测试包含较多布局、容量、shader 源文本及流送行为验证；没有覆盖真实启用 tessellation 材质的完整 GPU 帧，不能反证上传名称问题。
- 没有 fresh-cook 当前全部 shader，没有采集 GPU timestamp、寄存器/spill、barrier stall、cache miss 或 residency trace，也没有运行 UE 的同场景性能对照。因此本报告不会给出“慢 X 毫秒”“帧时快 Y%”。
- 没有修改产品源码、shader 或测试，没有重置原有未提交内容；仅添加此审查报告，来源记录保存在工作树外。

**14. 最终应达到的行为与验收依据。**

接受用户已确定的两项差异：atomic64 的 UE 风格 hybrid 主路径，以及仍保留 tessellation 的 no64 全 HW 路径；多 view 通用接口尽可能共享层级访问，VSM 只通过外部区域与输出适配接入。

最终应具有：空间粗层+view mask 的共享遍历、cluster 后的稀疏目标区域展开、内容决定的安全遍历层数、普通 SW 的快速 setup/有效 scanline、合批的 compact patch split、高利用率且局部复用的 dice、按实际 pattern 复用顶点的 HW tessellation、半尺寸起始且多 mip 融合的页 Hi-Z、由变化集合驱动的 VSM cache，以及真正覆盖真实渲染帧的批量上传和图结构复用。删除废弃的 software depth 和上传命名分叉；不增加兼容层。

验收需要围绕可观察行为，而不是源码里是否出现 wave/LDS/fuse 字样：

| 场景 | 应采集的数据 | 要排除的退化 |
|---|---|---|
| 单 view；同一 source view 的 1/32/256/1024 个 dirty region | root/node load 数、queue bytes、dispatch 数、overflow | 高层遍历随 region 数近似整倍复制 |
| 多个相近/相反方向 view、不同 LOD、不同遮挡历史 | node 访问共享率、各 view 可见集合、细分量 | mask 共享导致漏画，或全组采用最高细分导致过绘 |
| 全内裁剪、跨 near plane、细长三角形 | clip 调用数、edge test 数、VGPR/spill、SW/HW 分类 | 普通小三角形总走七平面 clip，scanline 扫空 bbox |
| Tess factors 1/2/4/8；单/多材质；hybrid/no64 | VS/domain 调用数、patch 数、barrier stalls、有效 lanes | 固定 192 padding、每角重复 domain、逐 row 串行 |
| 静态 VSM、小面积动态 caster、顶点变形、相机 clipmap 移动 | scanned instances、invalidated pages、rerendered static work、Hi-Z bytes | 漏失效、全 cache reset、巨大单线程页循环 |
| 不发生拓扑/驻留变化的正常帧；仅少量 streaming 更新 | CPU frame setup、allocations、Map/Flush 数、hazard 输入条数 | 仍每帧完整重建相同图/相同 resident ranges |
| 同场景 HW/SW 顺序与 overlap | 队列时间线、总 GPU frame time、带宽 | 仅开启 async 开关却没有 overlap，或 overlap 反而竞争变慢 |

普通平面、Phong/变形、alpha/pixel evaluation、运动矢量和 VSM 都需要在两条 raster 路径检查图像一致性。消除中间几何会把表面重建移到 shading；本地 Phong 使用两轮求解，UE displacement 也有专用 barycentric 重建，不能把“发生重建”本身列为额外错误。应单独检查残差、导数、motion 和相同画质下的成本。[本地 Phong 重建](F:/SomeEngine.Next-render-enhancement/assets/Shaders/phong_tessellation.slang:120)、[shade 与 motion 融合](F:/SomeEngine.Next-render-enhancement/assets/Shaders/cluster_shade_pipeline.slang:251)、[UE displacement barycentric 重建](https://github.com/EpicGames/UnrealEngine/blob/16d75d84714512edfb744e1fd0a59e9c74d57873/Engine/Shaders/Private/Nanite/NaniteVertexFactory.ush#L1063-L1087)。


**15. 补充：中间表示的逐字段可压缩性、引用化与消除。**

前述容量表不足以回答“压缩是否到位”。进一步追踪 producer、consumer 和字段范围后，结论是：**仍有较大的无损压缩空间，主要来自重复语义、阶段无关字段和全记录搬运。** 下面的目标大小是源码可推导的设计空间，尚未修改产品实现。位数检查数据记录在 [压缩位预算验证](F:/.codex/research/render-enhancement-20260905/compression-bit-budget.json)。

| 中间表示 | 当前 | 可行目标 | 成立条件与代价 |
|---|---:|---:|---|
| Split work patch | 24 B | **16 B** | 现有每 bin/每层调度使 bin、depth 隐含；work 的最终 edge factors 不需要保存 |
| Final patch | 24 B | **20 B** | 保留完整 3×32-bit barycentric；压紧 source/bin/factors；final 不再保存 depth |
| Binned final patch | 复制 24 B payload | **4 B patch index**，必要时 8 B index+pattern | 原 final pool 保持到 raster 完成；增加一次间接读取，减少 scatter 搬运 |
| Raster bin item | 8 B | **4 B** | 默认容量可直接 pack range；覆盖最大容量宜存 batch ordinal，或共享高位 |
| Shade pixel work item | 8 B/pixel | **4 B/pixel**；再按 quad/tile 分组 | 32-bit linear pixel 范围检查，或 view/高位放共享块头；quad收益取决于材质覆盖 |
| Deferred leaf item | 复制 12 B | **4 B candidate index（条件性）** | 依赖原 candidate pool 的生命期；node/leaf 混合队列需正确编码，不能直接统一改 stride |
| Traversal/visible/candidate | 12 B | 不能无条件宣称 8 B | 各字段总信息量超 64 bits；需要共享 instance/view 上下文、compact geometry reference 或更少记录 |
| VSM active page | 16 B | **8 B** | 只存 physical page + table index；source view/coarse 可由 table index 派生 |
| Per-page ClusterViewUniforms | 432 B/page | 消除整份复制 | source view 共用，page crop/输出区域推迟生成或紧凑描述 |
| Visibility winner | 8 B/pixel | **保留 8 B** | depth+winner id 原子一致更新；不应为了减半而退化正确性/深度精度 |
| 全局微顶点/微三角形 | 当前已不存在 | 无需再消除 | compact patch→局部求值→raster 已融合 |

**15.1 Split patch 24→16 B：可直接删除队列上下文里已有的信息。**

当前六个 uint 是：VisibleClusterIndex、TriangleAndRasterBin、三个 barycentric word、PackedState。按现有支持范围：

- VisibleClusterIndex 最大容量为 64×65535=4,194,240，22 bits 足够；默认 1,048,576 只用 20 bits。
- 原三角形字段当前保留 8 bits。
- 三个 corner 各两个 16-bit barycentric 分量，共 96 bits，不改变精度。
- split 当前按 CurrentBin 和 SplitLevel 调度；work patch 的 edge factors 在 seed/child 写入时都是 1，真正因子由处理该 patch 时计算。

因此 work payload 只需 `22-bit visible + 8-bit triangle + 3×32-bit barycentric = 126 bits`，放进 4 个 uint。bin 来自当前 dispatch；depth 来自层数。没有增加 source-table 间接读取，也没有降低曲面精度。若将来改为全局跨 bin split，不能继续假定 bin 隐含；可采用 20 B 的全局 work record，或按同特化的工作块共享 bin 信息。

两个 1 Mi-entry ping-pong work buffer 从 48 MiB 降为 32 MiB，单这项省 16 MiB 声明容量。每经过一个 split 层，读写每条工作项也少 8 bytes；实际 DRAM transaction 还受缓存/合并访问影响。

依据：[patch 字段](F:/SomeEngine.Next-render-enhancement/assets/Shaders/cluster_tessellation_types.slang:27)、[seed 中隐含 bin/初始状态](F:/SomeEngine.Next-render-enhancement/assets/Shaders/cluster_tessellation_core.slang:333)、[child 状态始终重置因子](F:/SomeEngine.Next-render-enhancement/assets/Shaders/cluster_tessellation_core.slang:409)、[逐层 split 的上下文](F:/SomeEngine.Next-render-enhancement/assets/Shaders/cluster_tessellation_core.slang:675)。UE 的 split payload 同样是 source triangle key+3 个 barycentric word 的 16-byte 数据。[UE 16-byte patch 编码](https://github.com/EpicGames/UnrealEngine/blob/16d75d84714512edfb744e1fd0a59e9c74d57873/Engine/Shaders/Private/Nanite/NaniteSplit.usf#L76-L87)。

**15.2 Final patch 24→20 B：即使保留 24-bit bin 和任意 1..8 的因子，也能无损放下。**

Final raster 不再需要 depth。其所需信息可以精确预算为：

| 字段 | bits |
|---|---:|
| Visible cluster index | 22 |
| 原 triangle id | 8 |
| Raster bin | 24 |
| 三条 edge factor，各存 factor−1，范围 0..7 | 9 |
| 三个压缩 barycentric corner | 96 |
| 合计 | **159 bits** |

159 bits 放进五个 uint，20 B。前 63 bits 的一个可行布局是 visible[21:0]、triangle[29:22]、bin[53:30]、factor0−1[56:54]、factor1−1[59:57]、factor2−1[62:60]，其后仍是三个原封不动的 barycentric uint。编码可用两个 32-bit word 做 shift/mask，不需要 atomic64 或 64-bit shader 算术能力。

已对 visible/triangle/bin 边界组合及全部 512 种 1..8 的 edge-factor 组合做了 13,824 组整数编码往返检查，全部一致；这仅证明位布局，不是 shader 集成测试。若只采用当前 power-of-two factors，还能用 6 bits 存三个指数，但无需依赖这一条件就能达到 20 B。

1 Mi final pool 从 24 MiB 降为 20 MiB。与两个 16 B work buffer 合起来，三份 payload 的声明容量由 72 MiB 变为 **52 MiB**。这个数字尚未计入任何额外 index buffer，也不是 allocator 的实际峰值结论。

**15.3 Final binning 不必复制 patch。**

当前 `ScatterTessellationBins` 将整条 patch 从 SourcePatches 复制到 GroupedPatches，dice/HW 再读 sorted payload。可改为 sorted patch indices，raster 用索引回读 final pool。排序不改变 patch 的内容，没必要搬动 barycentric 数据。

按当前 24 B payload，scatter 每条的目标写入从 24 B 变为 4 B，少 20 B；当前“复制后再消费”的 record 数据访问是读24+写24+读24=72 B，而索引版在最简单保守实现中读24+写4+读4+间接读24=56 B。若 scatter 只读取 bin 所在的 metadata word，逻辑 payload 更少。这些是访问字节模型，不是承诺 DRAM 必然同比下降：间接 gather 会影响 locality。

**不能据此额外宣称再省 20 MiB 峰值。** 当前 GroupedPatches 已复用一个 split work buffer；改索引后仍应复用该存储或让图按寿命 alias。若新增独立 4 MiB index buffer却不回收/复用原工作缓冲，部分容量收益会被抵消。优势最确定的是少写宽记录和缩小有序工作列表。

UE 的一种 permutation 正是 8-byte visible patch 引用+pattern，回到 16-byte split record 取 payload。当前实现的“最终因子已在 patch 内”允许仅存 4-byte index；另一种组织可用 index+pattern 8 B。[本地整条复制](F:/SomeEngine.Next-render-enhancement/assets/Shaders/cluster_tessellation_bin.slang:226)、[UE final patch 引用](https://github.com/EpicGames/UnrealEngine/blob/16d75d84714512edfb744e1fd0a59e9c74d57873/Engine/Shaders/Private/Nanite/NaniteSplit.usf#L265-L274)。

**15.4 Raster bin 8→4 B：这里必须区分默认范围和支持范围。**

当前每条是 `uint visibleIndex` 加 `uint packedStartEnd`，其中 start/end 各占 16 bits。实际 batch 长度最多 32，标准 cluster 最多 124 个三角形。

默认 MaxCandidates=2^20 时，`visible20 + triStart7 + (length−1)5 = 32 bits`，可直接 4 B，几次 shift/mask 即可；仍需保留 consumed/invalid batch 的编码，不能把当前用 range=0 标记 consumed 的行为丢掉。

但配置允许接近 2^22 个 candidates，这时同一布局需要 34 bits。不能为了 4 B 静默把 capacity 降到默认值。覆盖现有最大容量的另一条路线是 `visibleIndex22 + batchOrdinal4`，共 26 bits；batch ordinal 对应当前每 cluster 最多 9 个 emitted ranges，consumer 从既有 VRB/material range 信息恢复 start/count。保留一个未使用 ordinal 给 consumed 状态即可。代价是 batch-range decode，应该共享已有 cluster header 读取，不能让每个 HW 角点都重新运行整段 range 扫描。

默认 BinnedClusters 容量为 9 Mi entries，8→4 B 是 **72→36 MiB**，这是比缩几个 per-view padding 字段大得多的目标。UE 的 raster bin 自身也使用 uint2，因此这项是基于本地范围可以实现的优化，不能伪称 UE 全部使用 4 B。[当前 8-byte 写入](F:/SomeEngine.Next-render-enhancement/assets/Shaders/cluster_bin_io.slang:122)、[VRB/剩余 ranges 的生成](F:/SomeEngine.Next-render-enhancement/assets/Shaders/cluster_bin_scatter.slang:19)。

**15.5 Shade work list 8→4 B，再考虑 quad/tile 表示以减少条数。**

当前每 pixel 存 `uint2(packedXY16:16, viewIndex)`，view 被逐像素复制。若 `width×height×viewCount <= 2^32`，一个 linear pixel index 已足够无损恢复 x/y/view；超过此范围应在 block header 中共享 view 或高位，不能依靠 truncation。另一种做法是一个 block 对应一个 view，块内只存 32-bit XY。共享 view 的粒度可以是 work block，并不要求每个 view 重走整套 traversal 或每 view 单独提交 pass。

每 view 1080p：15.82→7.91 MiB；4K：63.28→31.64 MiB。随后还有“减少条数”的空间：同材质覆盖完整 quad/tile 时存 tile 坐标+覆盖 mask，再在 group 内展开，避免每个 pixel 都存记录；同时改善 triangle/vertex 数据局部性。混合材质、稀疏覆盖区域则保留紧凑 pixel/quad 条目，避免强行整 tile shading 导致多算。

UE 有 4-byte packed shading pixel，以及 8-byte quad+mask 和 full-tile/loose 工作组织。8-byte quad 能代表多个像素，不能仅比较“结构也是 uint2”就认为成本相同。[本地逐像素复制 view](F:/SomeEngine.Next-render-enhancement/assets/Shaders/cluster_bin_io.slang:178)、[UE pixel/quad packing](https://github.com/EpicGames/UnrealEngine/blob/16d75d84714512edfb744e1fd0a59e9c74d57873/Engine/Shaders/Private/Nanite/NaniteShadeBinning.usf#L184-L209)、[UE quad/tile 工作输出](https://github.com/EpicGames/UnrealEngine/blob/16d75d84714512edfb744e1fd0a59e9c74d57873/Engine/Shaders/Private/Nanite/NaniteShadeBinning.usf#L657-L676)。

**15.6 Traversal/candidate/visible 的 12→8 B，需要共享信息或改变引用，不能仅靠补几个 bitfield。**

DrawRequest 已经把 cluster12+view20 合为一字；另两字是 page byte offset 和 instance id。PageHeap 只保证 16-byte alignment，不能擅自按 64 KiB 页对齐右移 16 位。即使按低于2 GiB的地址范围、右移4位，page 仍约27 bits；再加 cluster12、view20 和独立 instance id，总信息量显然超过64 bits。

因此单条8 B必须建立新的事实：例如 page+local cluster 能用32-bit pool cluster reference表示，instance/view在已有批次或共享上下文里只需一个紧凑引用。代价是间接读取或队列分组，需看同一 instance/view 会重复多少 node/cluster，不能引入每条都唯一的新上下文表来假装节省。

Traversal 则更值得减少记录条数：若同一 node 对32个view可见，当前最多32×12=384 B。一个共享node/instance/view-group+mask记录即使16 B，再加一份共享组描述，也远小于384 B。实际收益取决于 surviving mask；如果只剩一个view，16 B反而比12 B更大。应按分歧程度选择展开时点，并保留各 view 的 LOD/遮挡结果。

Deferred leaf 可以只保存4-byte原candidate索引，但必须保留candidate pool到post消费结束；目前deferred同时承载node/leaf tagged union，并跨多个复用阶段，不能只改队列stride。多保活12 B原pool或阻止alias可能抵消节省。候选→剔除融合能彻底消除该中间写回，但需要更大的kernel及安全调度，UE默认逐层路径也保留candidate边界，不适合无条件融合。

UE 借鉴点是显式 packing 与按 permutation 添加所需字段：pool cluster reference、instance、view、flags，以及 VSM 才需的 page window。其 uint4 是打包函数的容器，不代表所有情况下都必须持久存16 B。[本地12 B工作记录](F:/SomeEngine.Next-render-enhancement/assets/Shaders/cluster_structures.slang:266)、[page只保证16 B对齐](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.Render.Cluster/PageHeap.cs:10)、[UE visible字段打包](https://github.com/EpicGames/UnrealEngine/blob/16d75d84714512edfb744e1fd0a59e9c74d57873/Engine/Shaders/Private/Nanite/NaniteDataDecode.ush#L369-L394)。

**15.7 VSM：先移除派生/重复信息，缓存标签不要随意量化。**

ActivePhysicalPages 当前存 physicalPage、tableIndex、sourceView、coarse四个uint；sourceView/coarse已有由tableIndex推导的函数，故可仅保留前两项，16→8 B。默认1024页只省8 KiB，是低成本整理，优先级低于上述MiB级队列。

更关键的是别为每页复制432 B完整ClusterViewUniforms。多个矩阵和相机/LOD配置属于source view；页只带目标区域和crop信息即可。按1024页算，完整view复制约432 KiB，绝对容量不大，但它在逐页高层遍历里被反复读取、增加cache压力；配合延后页展开，能一起减少记录和读取。

PageTable的world-page坐标和generation用于区分环形clipmap复用，不能仅因当前画面坐标小就截成16 bits。RequestedPages的32-bit frame stamp可以改bitset，但会引入clear、原子竞争和代际处理；它是另一种算法权衡，不是免费的32倍压缩。Hi-Z R32F改FP16也必须保证保守方向和误差，不能替代已经确定安全的“删掉原尺寸mip0”。

依据：[active page四字记录](F:/SomeEngine.Next-render-enhancement/assets/Shaders/vsm_allocate_pages.slang:159)、[source view/coarse可派生](F:/SomeEngine.Next-render-enhancement/assets/Shaders/vsm_allocate_pages.slang:114)、[432 B view](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.Render.Cluster.Pipeline/ClusterViewUniforms.cs:11)。

**15.8 Barycentric、winner和局部数据：不应通过降低语义来实现表面上的压缩。**

当前 patch 的 barycentric 已是每角两份16-bit，第三分量由和为1恢复。中间细分既有edge midpoint，也有centroid，因此不是所有点都落在1/256网格上。直接回到8-bit或机械换half会改变舍入、共享边一致性和小patch精度。

可以研究“根三角形+细分操作路径”来程序化重建corners：最多5层，每层保存split模式+child序号，可显著减少96-bit坐标。但每个消费者要重放路径，必须逐层保持现有量化规则；这属于用ALU换带宽的候选方案，不能等同立即可用的无损bit packing。优先先做上面不改变barycentric的16/20 B格式。

Winner的64 bits维持depth和id一起决定胜者，应保留。可以研究直接从winner解码id的消费者以删除独立VisBuffer副本，但可能把后续4 B读取变为8 B读取；D32 scene depth也有HW/Hi-Z消费者，不能因winner带depth就假定其他depth资源可全删。当前no64路径仍使用固定功能depth比较，压缩不能变相恢复分裂的32-bit原子depth/id协议。

局部寄存器/LDS的压缩同样要看访问路径：UE dice在寄存器中使用subpixel xy+depth的紧凑值；本地rolling LDS仅288 B，继续省其几十bytes的价值远小于消除17次barrier和低利用率。不要为了统一格式把所有计算都放在packed整数中；在全局读写边界压缩、消费者局部解码通常更简单。

**15.9 新发现：已有visible记录压缩未贯穿所有消费者。**

当前 producer和图绑定使用12-byte DrawRequest：x=pageOffset、y=instanceID、z=clusterAndView。但 `shade_bin_count.slang`、`shade_bin_scatter.slang` 仍声明 `StructuredBuffer<uint4>`，按旧x=page、y=cluster、z=instance、w=view读取；`shade_bin_common.slang` 也按旧字段解释。

这是压缩集成缺陷，会造成资源布局不一致或错误索引/view过滤，不能把“16→12已经做了”当成全链路优化完成。应统一为当前DrawRequest及其decode函数，删除旧uint4解释；不增加兼容转换缓冲，否则会把已省的空间和带宽补回去。本次只报告，尚未修改shader或运行GPU复现。

依据：[producer的新布局](F:/SomeEngine.Next-render-enhancement/assets/Shaders/cluster_structures.slang:268)、[旧count消费者](F:/SomeEngine.Next-render-enhancement/assets/Shaders/shade_bin_count.slang:10)、[旧scatter消费者](F:/SomeEngine.Next-render-enhancement/assets/Shaders/shade_bin_scatter.slang:9)、[图仍绑定12 B stride](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.Render.Cluster.Pipeline/ClusterPipelineSystem.Passes.cs:2712)。

按当前默认容量，最值得落实的压缩目标是split16 B+final20 B、final bin只搬index、raster bin4 B、shade work4 B或quad/tile表示，并把view共享带来的“少生成记录”纳入同一评估。仅payload容量，tess三份可少20 MiB、raster bin少36 MiB、4K单view shade work少31.64 MiB；这些数可以用于预算，**不能不看生命期、alias与额外引用存储就相加宣布GPU峰值下降87.64 MiB**。
